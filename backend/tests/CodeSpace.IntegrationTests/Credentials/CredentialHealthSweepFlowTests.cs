using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Auth;
using CodeSpace.Core.Services.Providers.Identity;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit.Abstractions;

namespace CodeSpace.IntegrationTests.Credentials;

/// <summary>
/// 🟢 High fidelity — the recurring credential health probe, from the real <c>ProbeCredentialsCommand</c> through the real
/// GitLab provider (NGitLab and raw HTTP, with the credential in the header its type names) to the credential row, over real
/// Postgres. The one fake is GitLab, a <see cref="GitLabDoorkeeperStub"/> that answers 401 to a token that was revoked or
/// reached its expiry, describes a token at <c>personal_access_tokens/self</c>, and honours OAuth grants as Doorkeeper does.
///
/// <para>What it pins: a personal, project or group token that expired or was revoked at GitLab is found without a run
/// having to fail on it — marked <c>Error</c> with a reason, so its next use fails fast and typed; one GitLab accepts is
/// validated and its real expiry recorded (a token linked before anything recorded one gets it now); an answer that is not
/// an answer — GitLab unreachable — changes nothing; and a credential marked Error is asked again only after an hour, healing
/// by itself if GitLab accepts it again.</para>
///
/// <para>The sweep is deployment-wide, so each test says which credentials are its own and records every other as validated
/// (<see cref="GitLabCredentialSeed.QuietEveryOtherCredentialAsync"/>): a test asserts the rows it owns, never the tally.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class CredentialHealthSweepFlowTests
{
    private readonly PostgresFixture _fixture;
    private readonly ITestOutputHelper _output;

    public CredentialHealthSweepFlowTests(PostgresFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task A_token_gitlab_accepts_is_validated_with_its_real_expiry_and_one_it_refuses_is_marked_and_fails_fast_afterwards()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var expires = DateTimeOffset.UtcNow.AddDays(10);
        gitlab.AddAccessToken("glpat-live", "nacho", expires);
        gitlab.AddAccessToken("glpat-revoked", "nacho");
        gitlab.RevokeAccessToken("glpat-revoked");
        var live = await GitLabCredentialSeed.SeedAccessTokenAsync(_fixture, gitlab.BaseUrl, "glpat-live").ConfigureAwait(false);
        var recordedExpiry = AsStored(DateTimeOffset.UtcNow.AddDays(20));
        var revoked = await GitLabCredentialSeed.SeedAccessTokenAsync(_fixture, gitlab.BaseUrl, "glpat-revoked", expiresDate: recordedExpiry).ConfigureAwait(false);
        await GitLabCredentialSeed.QuietEveryOtherCredentialAsync(_fixture, live.CredentialId, revoked.CredentialId).ConfigureAwait(false);

        await SweepAsync().ConfigureAwait(false);

        Dump(gitlab);
        var liveRow = await GitLabCredentialSeed.ReadCredentialAsync(_fixture, live.CredentialId).ConfigureAwait(false);

        liveRow.Status.ShouldBe(CredentialStatus.Active);
        liveRow.LastValidatedDate.ShouldNotBeNull().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-2));
        liveRow.ExpiresDate.ShouldBe(new DateTimeOffset(expires.UtcDateTime.Date, TimeSpan.Zero), "GitLab's expires_at is a date; the row records the instant it begins");
        liveRow.LastError.ShouldBeNull();

        var revokedRow = await GitLabCredentialSeed.ReadCredentialAsync(_fixture, revoked.CredentialId).ConfigureAwait(false);

        revokedRow.Status.ShouldBe(CredentialStatus.Error);
        revokedRow.LastError.ShouldBe("token rejected by GitLab");
        revokedRow.ExpiresDate.ShouldBe(recordedExpiry, "a verdict does not rewrite what was recorded about the token's expiry");
        revokedRow.LastValidatedDate.ShouldBeNull();

        var asked = gitlab.Log.Count;
        var failure = await Should.ThrowAsync<CredentialNeedsReconnectException>(() => ResolveAsync(revoked.RepositoryId)).ConfigureAwait(false);

        failure.CredentialId.ShouldBe(revoked.CredentialId);
        failure.Message.ShouldContain("token rejected by GitLab");
        gitlab.Log.Count.ShouldBe(asked, "the failure comes from what the row says: GitLab is not asked again");
        (await ResolveAsync(live.RepositoryId).ConfigureAwait(false)).Token.ShouldBe("glpat-live", "an accepted token resolves as it always did");
    }

    [Fact]
    public async Task A_probe_that_gets_no_answer_changes_nothing()
    {
        // GitLab is unreachable: nothing is known about the token, so nothing is recorded — least of all that it is dead.
        var gitlab = new GitLabDoorkeeperStub();
        var seed = await GitLabCredentialSeed.SeedAccessTokenAsync(_fixture, gitlab.BaseUrl, "glpat-any").ConfigureAwait(false);
        gitlab.Dispose();
        await GitLabCredentialSeed.QuietEveryOtherCredentialAsync(_fixture, seed.CredentialId).ConfigureAwait(false);

        await SweepAsync().ConfigureAwait(false);

        var row = await GitLabCredentialSeed.ReadCredentialAsync(_fixture, seed.CredentialId).ConfigureAwait(false);

        row.Status.ShouldBe(CredentialStatus.Active);
        row.LastValidatedDate.ShouldBeNull("it is still due: the next tick asks again");
        row.LastError.ShouldBeNull();
    }

    [Fact]
    public async Task A_pass_is_bounded_by_its_batch_and_the_next_one_takes_up_where_the_validated_left_off()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var seeds = new List<SeededGitLabCredential>();

        for (var i = 0; i < 3; i++)
        {
            gitlab.AddAccessToken($"glpat-batch-{i}", "nacho");
            seeds.Add(await GitLabCredentialSeed.SeedAccessTokenAsync(_fixture, gitlab.BaseUrl, $"glpat-batch-{i}").ConfigureAwait(false));
        }

        await GitLabCredentialSeed.QuietEveryOtherCredentialAsync(_fixture, seeds.Select(s => s.CredentialId).ToArray()).ConfigureAwait(false);

        await SweepAsync(batchSize: 2).ConfigureAwait(false);
        (await CountValidatedAsync(seeds).ConfigureAwait(false)).ShouldBe(2, "a pass probes at most its batch");

        await SweepAsync(batchSize: 2).ConfigureAwait(false);
        (await CountValidatedAsync(seeds).ConfigureAwait(false)).ShouldBe(3, "a validated credential is not a candidate, so the next pass reaches the one left");
    }

    [Fact]
    public async Task A_credential_validated_recently_is_not_asked_about_and_one_validated_long_ago_is()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        gitlab.AddAccessToken("glpat-recent", "nacho");
        gitlab.AddAccessToken("glpat-stale", "nacho");
        var recent = await GitLabCredentialSeed.SeedAccessTokenAsync(_fixture, gitlab.BaseUrl, "glpat-recent").ConfigureAwait(false);
        var stale = await GitLabCredentialSeed.SeedAccessTokenAsync(_fixture, gitlab.BaseUrl, "glpat-stale").ConfigureAwait(false);
        var validatedHourAgo = AsStored(DateTimeOffset.UtcNow.AddHours(-1));
        await GitLabCredentialSeed.SetLastValidatedAsync(_fixture, recent.CredentialId, validatedHourAgo).ConfigureAwait(false);
        await GitLabCredentialSeed.SetLastValidatedAsync(_fixture, stale.CredentialId, DateTimeOffset.UtcNow.AddHours(-7)).ConfigureAwait(false);
        await GitLabCredentialSeed.QuietEveryOtherCredentialAsync(_fixture, recent.CredentialId, stale.CredentialId).ConfigureAwait(false);
        await GitLabCredentialSeed.SetLastValidatedAsync(_fixture, recent.CredentialId, validatedHourAgo).ConfigureAwait(false);
        await GitLabCredentialSeed.SetLastValidatedAsync(_fixture, stale.CredentialId, DateTimeOffset.UtcNow.AddHours(-7)).ConfigureAwait(false);

        await SweepAsync().ConfigureAwait(false);

        (await GitLabCredentialSeed.ReadCredentialAsync(_fixture, recent.CredentialId).ConfigureAwait(false)).LastValidatedDate.ShouldBe(validatedHourAgo, "validated within the last six hours: GitLab is not asked");
        (await GitLabCredentialSeed.ReadCredentialAsync(_fixture, stale.CredentialId).ConfigureAwait(false)).LastValidatedDate.ShouldNotBeNull().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-2));
        gitlab.Count(StubEvent.TokenApi200).ShouldBe(2, "one user call and one self call, for the stale credential only");
    }

    [Fact]
    public async Task A_credential_marked_Error_is_asked_again_only_after_an_hour_and_heals_when_gitlab_accepts_it()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        gitlab.AddAccessToken("glpat-back", "nacho");
        var seed = await GitLabCredentialSeed.SeedAccessTokenAsync(_fixture, gitlab.BaseUrl, "glpat-back").ConfigureAwait(false);
        await MarkErrorAsync(seed.CredentialId, "token rejected by GitLab").ConfigureAwait(false);
        await GitLabCredentialSeed.QuietEveryOtherCredentialAsync(_fixture, seed.CredentialId).ConfigureAwait(false);

        await SweepAsync().ConfigureAwait(false);

        (await GitLabCredentialSeed.ReadCredentialAsync(_fixture, seed.CredentialId).ConfigureAwait(false)).Status.ShouldBe(CredentialStatus.Error, "marked a moment ago: not due");
        gitlab.Count(StubEvent.TokenApi200).ShouldBe(0, "GitLab was not asked");

        await GitLabCredentialSeed.SetLastModifiedAsync(_fixture, seed.CredentialId, DateTimeOffset.UtcNow.AddHours(-2)).ConfigureAwait(false);

        await SweepAsync().ConfigureAwait(false);

        var healed = await GitLabCredentialSeed.ReadCredentialAsync(_fixture, seed.CredentialId).ConfigureAwait(false);

        healed.Status.ShouldBe(CredentialStatus.Active, "GitLab accepts the token again — an account unblocked, an expiry extended — so the Error is lifted");
        healed.LastError.ShouldBeNull();
        healed.LastValidatedDate.ShouldNotBeNull();
        (await ResolveAsync(seed.RepositoryId).ConfigureAwait(false)).Token.ShouldBe("glpat-back", "and the token is usable again");
    }

    [Fact]
    public async Task A_credential_still_refused_after_an_hour_keeps_its_first_reason_and_waits_another_hour()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        gitlab.AddAccessToken("glpat-gone", "nacho");
        gitlab.RevokeAccessToken("glpat-gone");
        var seed = await GitLabCredentialSeed.SeedAccessTokenAsync(_fixture, gitlab.BaseUrl, "glpat-gone").ConfigureAwait(false);
        await MarkErrorAsync(seed.CredentialId, "needs reconnect: invalid_grant").ConfigureAwait(false);
        await GitLabCredentialSeed.SetLastModifiedAsync(_fixture, seed.CredentialId, DateTimeOffset.UtcNow.AddHours(-2)).ConfigureAwait(false);
        await GitLabCredentialSeed.QuietEveryOtherCredentialAsync(_fixture, seed.CredentialId).ConfigureAwait(false);

        await SweepAsync().ConfigureAwait(false);

        var row = await GitLabCredentialSeed.ReadCredentialAsync(_fixture, seed.CredentialId).ConfigureAwait(false);

        row.Status.ShouldBe(CredentialStatus.Error);
        row.LastError.ShouldBe("needs reconnect: invalid_grant", "the first reason is the most specific one");
        row.LastModifiedDate.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-2), "the confirmation is dated, which is what the next recheck is measured from");
    }

    [Fact]
    public async Task A_tokens_expiry_is_recorded_when_it_was_unknown_and_cleared_when_gitlab_says_it_never_expires()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var expires = DateTimeOffset.UtcNow.AddDays(10);
        gitlab.AddAccessToken("glpat-dated", "nacho", expires);
        gitlab.AddAccessToken("glpat-forever", "nacho");
        var dated = await GitLabCredentialSeed.SeedAccessTokenAsync(_fixture, gitlab.BaseUrl, "glpat-dated", AuthType.ProjectAccessToken).ConfigureAwait(false);
        var forever = await GitLabCredentialSeed.SeedAccessTokenAsync(_fixture, gitlab.BaseUrl, "glpat-forever", AuthType.GroupAccessToken, expiresDate: DateTimeOffset.UtcNow.AddDays(5)).ConfigureAwait(false);
        await GitLabCredentialSeed.QuietEveryOtherCredentialAsync(_fixture, dated.CredentialId, forever.CredentialId).ConfigureAwait(false);

        await SweepAsync().ConfigureAwait(false);

        (await GitLabCredentialSeed.ReadCredentialAsync(_fixture, dated.CredentialId).ConfigureAwait(false)).ExpiresDate.ShouldBe(new DateTimeOffset(expires.UtcDateTime.Date, TimeSpan.Zero), "a token linked before anything recorded its expiry gets it from the sweep");
        (await GitLabCredentialSeed.ReadCredentialAsync(_fixture, forever.CredentialId).ConfigureAwait(false)).ExpiresDate.ShouldBeNull("GitLab described the token and gave it no expiry: what was recorded before is not true");
    }

    [Fact]
    public async Task An_oauth_credential_is_validated_without_touching_its_access_tokens_expiry_and_a_dead_grant_is_marked_by_the_refusal_itself()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        gitlab.Seed(GitLabDoorkeeperStub.DefaultClientId, "nacho", "at-0", "rt-0");
        var live = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-0", "rt-0", expired: false).ConfigureAwait(false);
        var dead = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-dead", "rt-dead").ConfigureAwait(false);
        var accessTokenExpiry = (await GitLabCredentialSeed.ReadCredentialAsync(_fixture, live.CredentialId).ConfigureAwait(false)).ExpiresDate;
        await GitLabCredentialSeed.QuietEveryOtherCredentialAsync(_fixture, live.CredentialId, dead.CredentialId).ConfigureAwait(false);

        await SweepAsync().ConfigureAwait(false);

        Dump(gitlab);
        var liveRow = await GitLabCredentialSeed.ReadCredentialAsync(_fixture, live.CredentialId).ConfigureAwait(false);

        liveRow.Status.ShouldBe(CredentialStatus.Active);
        liveRow.LastValidatedDate.ShouldNotBeNull();
        liveRow.ExpiresDate.ShouldBe(accessTokenExpiry, "an OAuth credential's expiry is its access token's, which the refresher writes");

        var deadRow = await GitLabCredentialSeed.ReadCredentialAsync(_fixture, dead.CredentialId).ConfigureAwait(false);

        deadRow.Status.ShouldBe(CredentialStatus.Error);
        deadRow.LastError.ShouldBe("needs reconnect: invalid_grant", "the refusal that found it is what the row says, not the probe's generic reason");
    }

    [Fact]
    public async Task A_disconnected_or_retired_credential_is_never_probed()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        gitlab.AddAccessToken("glpat-disconnected", "nacho");
        gitlab.AddAccessToken("glpat-retired", "nacho");
        var disconnected = await GitLabCredentialSeed.SeedAccessTokenAsync(_fixture, gitlab.BaseUrl, "glpat-disconnected").ConfigureAwait(false);
        var retired = await GitLabCredentialSeed.SeedAccessTokenAsync(_fixture, gitlab.BaseUrl, "glpat-retired").ConfigureAwait(false);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();

            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE credential SET status = 'Revoked', encrypted_payload = '' WHERE id = {disconnected.CredentialId}").ConfigureAwait(false);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE credential SET deleted_date = now() WHERE id = {retired.CredentialId}").ConfigureAwait(false);
        }

        await GitLabCredentialSeed.QuietEveryOtherCredentialAsync(_fixture, disconnected.CredentialId, retired.CredentialId).ConfigureAwait(false);

        await SweepAsync().ConfigureAwait(false);

        gitlab.Log.ShouldBeEmpty("neither has a token to present, so GitLab was never asked");
        (await GitLabCredentialSeed.ReadCredentialAsync(_fixture, disconnected.CredentialId).ConfigureAwait(false)).Status.ShouldBe(CredentialStatus.Revoked);
    }

    [Fact]
    public async Task A_verdict_is_written_only_over_the_payload_it_judged_and_never_into_a_disconnected_row()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var seed = await GitLabCredentialSeed.SeedAccessTokenAsync(_fixture, gitlab.BaseUrl, "glpat-judged").ConfigureAwait(false);
        var judged = (await GitLabCredentialSeed.ReadCredentialAsync(_fixture, seed.CredentialId).ConfigureAwait(false)).EncryptedPayload;

        using var scope = _fixture.BeginScope();
        var writer = scope.Resolve<ICredentialHealthWriter>();

        (await writer.MarkRejectedAsync(seed.CredentialId, "a payload the row no longer holds", "token rejected by GitLab", CancellationToken.None).ConfigureAwait(false)).ShouldBeFalse("a reconnect replaced the row since the verdict was judged");
        (await GitLabCredentialSeed.ReadCredentialAsync(_fixture, seed.CredentialId).ConfigureAwait(false)).Status.ShouldBe(CredentialStatus.Active);

        (await writer.MarkRejectedAsync(seed.CredentialId, judged, "token rejected by GitLab", CancellationToken.None).ConfigureAwait(false)).ShouldBeTrue();
        (await writer.MarkRejectedAsync(seed.CredentialId, judged, "a later, vaguer reason", CancellationToken.None).ConfigureAwait(false)).ShouldBeTrue();
        (await GitLabCredentialSeed.ReadCredentialAsync(_fixture, seed.CredentialId).ConfigureAwait(false)).LastError.ShouldBe("token rejected by GitLab", "the first reason stays");

        (await writer.MarkValidatedAsync(seed.CredentialId, judged, DateTimeOffset.UtcNow, expiry: null, CancellationToken.None).ConfigureAwait(false)).ShouldBeTrue();

        var healed = await GitLabCredentialSeed.ReadCredentialAsync(_fixture, seed.CredentialId).ConfigureAwait(false);

        healed.Status.ShouldBe(CredentialStatus.Active);
        healed.LastError.ShouldBeNull();

        await _fixture.BeginScope().Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"UPDATE credential SET status = 'Revoked', encrypted_payload = '' WHERE id = {seed.CredentialId}").ConfigureAwait(false);

        (await writer.MarkRejectedAsync(seed.CredentialId, string.Empty, "token rejected by GitLab", CancellationToken.None).ConfigureAwait(false)).ShouldBeFalse("a disconnected row is not this verdict's to change");
        (await writer.MarkValidatedAsync(seed.CredentialId, string.Empty, DateTimeOffset.UtcNow, expiry: null, CancellationToken.None).ConfigureAwait(false)).ShouldBeFalse();
        (await GitLabCredentialSeed.ReadCredentialAsync(_fixture, seed.CredentialId).ConfigureAwait(false)).Status.ShouldBe(CredentialStatus.Revoked);
    }

    [Fact]
    public async Task Linking_a_token_records_when_gitlab_says_it_expires_and_that_it_was_just_validated()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var expires = DateTimeOffset.UtcNow.AddDays(3);
        gitlab.AddAccessToken("glpat-linked", "nacho", expires);
        var team = await WorkflowsTestSeed.SeedTeamAsync(_fixture).ConfigureAwait(false);
        var seed = await GitLabCredentialSeed.SeedAccessTokenAsync(_fixture, gitlab.BaseUrl, "glpat-other", existingTeam: team).ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(team.UserId, team.TeamId, CodeSpace.Messages.Constants.Roles.Admin);
        var identity = await scope.Resolve<IUserProviderIdentityService>().LinkByPatAsync(seed.InstanceId, "glpat-linked", CancellationToken.None).ConfigureAwait(false);

        var credential = await scope.Resolve<CodeSpaceDbContext>().UserProviderIdentity.AsNoTracking().Where(i => i.Id == identity.Id).Select(i => i.Credential).SingleAsync().ConfigureAwait(false);

        credential.ExpiresDate.ShouldBe(new DateTimeOffset(expires.UtcDateTime.Date, TimeSpan.Zero));
        credential.LastValidatedDate.ShouldNotBeNull().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-2));
        credential.Status.ShouldBe(CredentialStatus.Active);
    }

    // ─── Plumbing ──────────────────────────────────────────────────────────────

    private async Task<int> SweepAsync(int batchSize = 25)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<IMediator>().Send(new ProbeCredentialsCommand { BatchSize = batchSize }).ConfigureAwait(false);
    }

    private async Task<ResolvedAuth> ResolveAsync(Guid repositoryId)
    {
        using var scope = _fixture.BeginScope();
        var repo = await CredentialRefreshHarness.LoadUntrackedAsync(scope, repositoryId).ConfigureAwait(false);

        return await scope.Resolve<IProviderAuthResolver>().ResolveAsync(new ProviderContext(repo.ProviderInstance, repo.Credential!), CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<int> CountValidatedAsync(IEnumerable<SeededGitLabCredential> seeds)
    {
        var validated = 0;

        foreach (var seed in seeds)
            if ((await GitLabCredentialSeed.ReadCredentialAsync(_fixture, seed.CredentialId).ConfigureAwait(false)).LastValidatedDate is { } at && at > DateTimeOffset.UtcNow.AddMinutes(-2)) validated++;

        return validated;
    }

    private async Task MarkErrorAsync(Guid credentialId, string lastError)
    {
        using var scope = _fixture.BeginScope();

        await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"UPDATE credential SET status = 'Error', last_error = {lastError}, last_modified_date = now() WHERE id = {credentialId}").ConfigureAwait(false);
    }

    private void Dump(GitLabDoorkeeperStub gitlab)
    {
        foreach (var entry in gitlab.Log) _output.WriteLine($"  {entry.At:HH:mm:ss.fff} {entry.Kind}: {entry.Detail}");
    }

    /// <summary>A timestamp as Postgres stores it: timestamptz keeps microseconds, a .NET clock on Linux reads 100ns ticks, so an expected value must be cut to what the row can hold.</summary>
    private static DateTimeOffset AsStored(DateTimeOffset value) => new(value.Ticks - value.Ticks % TimeSpan.TicksPerMicrosecond, value.Offset);
}
