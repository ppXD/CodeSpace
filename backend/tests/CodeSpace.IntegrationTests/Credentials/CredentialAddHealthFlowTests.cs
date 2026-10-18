using System.Diagnostics;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Webhooks;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Credentials;

/// <summary>
/// 🟢 High fidelity — a token added through the credentials API (<c>POST /api/credentials/pat</c> and
/// <c>/group-access-token</c>: personal, and the group token a team shares) is described by its provider as it is added, as a
/// token linked for a person already is: GitLab says when it expires (every new token has an expiry, and one that reaches it
/// answers 401 to everything), and the credential records that and the validation, so the list says <c>expiring</c> from the
/// first read instead of waiting for the sweep's first pass. The real <c>AddCredentialCommand</c> pipeline, the real GitLab
/// provider over loopback, real Postgres.
///
/// <para>Adding never depends on the answer: a provider that is unreachable, silent or refuses the token leaves the credential
/// added as it always was, unstamped and due for the sweep — which is what marks a token GitLab refuses.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class CredentialAddHealthFlowTests
{
    private readonly PostgresFixture _fixture;

    public CredentialAddHealthFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Theory]
    [InlineData(AuthType.Pat, CredentialOwnership.Personal)]
    [InlineData(AuthType.GroupAccessToken, CredentialOwnership.TeamService)]
    public async Task Adding_a_token_records_when_gitlab_says_it_expires_and_that_it_was_just_validated(AuthType type, CredentialOwnership ownership)
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var expires = DateTimeOffset.UtcNow.AddDays(3);
        gitlab.AddAccessToken("glpat-added", "nacho", expires);
        var (team, seed) = await SeedAsync(gitlab.BaseUrl).ConfigureAwait(false);

        var id = await AddAsync(team, seed, "glpat-added", type, ownership).ConfigureAwait(false);

        var row = await GitLabCredentialSeed.ReadCredentialAsync(_fixture, id).ConfigureAwait(false);

        row.Status.ShouldBe(CredentialStatus.Active);
        row.ExpiresDate.ShouldBe(new DateTimeOffset(expires.UtcDateTime.Date, TimeSpan.Zero), "GitLab's expires_at is a date; the row records the instant it begins");
        row.LastValidatedDate.ShouldNotBeNull().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-2));
        row.LastError.ShouldBeNull();
    }

    [Fact]
    public async Task Adding_a_token_gitlab_says_never_expires_records_no_expiry()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        gitlab.AddAccessToken("glpat-forever", "nacho");
        var (team, seed) = await SeedAsync(gitlab.BaseUrl).ConfigureAwait(false);

        var row = await GitLabCredentialSeed.ReadCredentialAsync(_fixture, await AddAsync(team, seed, "glpat-forever").ConfigureAwait(false)).ConfigureAwait(false);

        row.ExpiresDate.ShouldBeNull();
        row.LastValidatedDate.ShouldNotBeNull("GitLab accepted it, which is worth recording even with no expiry to say");
    }

    [Fact]
    public async Task A_token_added_while_the_provider_is_unreachable_is_added_as_it_always_was_and_left_due_for_the_sweep()
    {
        var gitlab = new GitLabDoorkeeperStub();
        var (team, seed) = await SeedAsync(gitlab.BaseUrl).ConfigureAwait(false);
        gitlab.Dispose();

        var row = await GitLabCredentialSeed.ReadCredentialAsync(_fixture, await AddAsync(team, seed, "glpat-offline").ConfigureAwait(false)).ConfigureAwait(false);

        row.Status.ShouldBe(CredentialStatus.Active);
        row.LastValidatedDate.ShouldBeNull("nothing was learned, so it is still due");
        row.ExpiresDate.ShouldBeNull();
    }

    [Fact]
    public async Task A_token_added_while_the_provider_is_silent_is_added_within_the_probe_deadline_and_left_due_for_the_sweep()
    {
        using var silent = new SilentProviderHost();
        var (team, seed) = await SeedAsync(silent.BaseUrl).ConfigureAwait(false);
        var clock = Stopwatch.StartNew();

        var id = await AddAsync(team, seed, "glpat-silent", deadlines: new CredentialHealthDeadlines(Probe: TimeSpan.FromSeconds(1), Pass: TimeSpan.FromMinutes(1))).ConfigureAwait(false);

        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(15), "adding a token does not wait on a provider that has stopped answering");

        var row = await GitLabCredentialSeed.ReadCredentialAsync(_fixture, id).ConfigureAwait(false);

        row.Status.ShouldBe(CredentialStatus.Active);
        row.LastValidatedDate.ShouldBeNull();
    }

    [Fact]
    public async Task A_token_gitlab_refuses_is_still_added_and_the_sweep_is_what_marks_it()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        gitlab.AddAccessToken("glpat-revoked", "nacho");
        gitlab.RevokeAccessToken("glpat-revoked");
        var (team, seed) = await SeedAsync(gitlab.BaseUrl).ConfigureAwait(false);

        var id = await AddAsync(team, seed, "glpat-revoked").ConfigureAwait(false);

        var added = await GitLabCredentialSeed.ReadCredentialAsync(_fixture, id).ConfigureAwait(false);

        added.Status.ShouldBe(CredentialStatus.Active, "adding has never validated the token against its provider and still does not refuse one");
        added.LastValidatedDate.ShouldBeNull("a refusal is not a validation");

        await GitLabCredentialSeed.QuietEveryOtherCredentialAsync(_fixture, id).ConfigureAwait(false);

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IMediator>().Send(new ProbeCredentialsCommand()).ConfigureAwait(false);

        (await GitLabCredentialSeed.ReadCredentialAsync(_fixture, id).ConfigureAwait(false)).Status.ShouldBe(CredentialStatus.Error, "the sweep found out within its first pass");
    }

    // ─── Plumbing ──────────────────────────────────────────────────────────────

    private async Task<((Guid TeamId, Guid UserId) Team, SeededGitLabCredential Seed)> SeedAsync(string gitLabBaseUrl)
    {
        var team = await WorkflowsTestSeed.SeedTeamAsync(_fixture).ConfigureAwait(false);
        var seed = await GitLabCredentialSeed.SeedAccessTokenAsync(_fixture, gitLabBaseUrl, "glpat-other", existingTeam: team).ConfigureAwait(false);

        return (team, seed);
    }

    /// <summary><c>POST /api/credentials/pat</c> or <c>/group-access-token</c>, as the team's Admin.</summary>
    private async Task<Guid> AddAsync((Guid TeamId, Guid UserId) team, SeededGitLabCredential seed, string token, AuthType type = AuthType.Pat, CredentialOwnership ownership = CredentialOwnership.Personal, CredentialHealthDeadlines? deadlines = null)
    {
        CredentialPayload payload = type == AuthType.GroupAccessToken ? new GroupAccessTokenPayload { Token = token } : new PatPayload { Token = token };

        using var scope = _fixture.BeginScopeAs(team.UserId, team.TeamId, Roles.Admin);
        using var run = deadlines is null ? null : scope.BeginLifetimeScope(b => b.RegisterInstance(deadlines).SingleInstance());

        return await (run ?? scope).Resolve<IMediator>().Send(new AddCredentialCommand { ProviderInstanceId = seed.InstanceId, DisplayName = $"added {token}", Payload = payload, Ownership = ownership }).ConfigureAwait(false);
    }
}
