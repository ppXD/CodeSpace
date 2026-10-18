using Autofac;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Settings.Database;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Webhooks;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using MediatR;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Credentials;

/// <summary>
/// Where an in-place re-authorization meets what the credential health work records about a credential: a refusal marks it
/// <c>Error</c> and the list derives <c>health</c> from the row, so a re-authorization has to leave the row saying what is true
/// of the NEW token — active, validated now, expiring when the provider says so — and nothing that judged the old one may land
/// afterwards. The one fake is GitLab, a <see cref="GitLabDoorkeeperStub"/>; the rest is production, from the mediator down to
/// Postgres, with the credential read the way a run and the Providers list read it.
/// </summary>
public partial class CredentialReauthorizationFlowTests
{
    // ─── What the list and a run say once the credential is re-authorized ─────────────────────────────────────

    [Fact]
    public async Task A_grant_the_provider_refused_is_reconnected_in_place_and_reads_active_and_resolves_at_once()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        // A pair GitLab never issued, past its expiry: the first run's refresh is refused for good, as a revoked grant is.
        var credentialId = await SeedOAuthCredentialAsync(world, world.NachoId, "at-dead", "rt-dead", expired: true).ConfigureAwait(false);
        var repositoryId = await SeedRepositoryAsync(world, credentialId, RepositoryStatus.Active).ConfigureAwait(false);
        await SeedIdentityAsync(world, world.NachoId, credentialId, "nacho").ConfigureAwait(false);

        await Should.ThrowAsync<CredentialNeedsReconnectException>(() => CredentialRefreshHarness.ResolveTokenAsync(_fixture, repositoryId)).ConfigureAwait(false);

        gitlab.Count(StubEvent.Refused).ShouldBe(1, "fixture check: GitLab refused the dead refresh token, which is what marks the credential");
        var refused = await ListedAsync(world, world.NachoId, credentialId).ConfigureAwait(false);
        refused.ShouldSatisfyAllConditions(c => c.Status.ShouldBe(CredentialStatus.Error), c => c.LastError.ShouldBe("needs reconnect: invalid_grant"), c => c.Health.ShouldBe(CredentialHealth.Rejected));

        var finished = await ReconnectAsync(world, world.NachoId, credentialId, "authz:nacho").ConfigureAwait(false);

        Dump(gitlab);
        finished.CredentialId.ShouldBe(credentialId, "the same row: nothing moved, so the connect-time rule for a refused credential's bindings had nothing to do");
        (await CountCredentialsAsync(world).ConfigureAwait(false)).ShouldBe(1);

        var row = await LoadCredentialAsync(credentialId).ConfigureAwait(false);
        row.ShouldSatisfyAllConditions(r => r.Status.ShouldBe(CredentialStatus.Active), r => r.LastError.ShouldBeNull(), r => r.DeletedDate.ShouldBeNull(), r => r.LastValidatedDate.ShouldNotBeNull().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-2)), r => r.ExpiresDate.ShouldNotBeNull().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddHours(1)));

        var listed = await ListedAsync(world, world.NachoId, credentialId).ConfigureAwait(false);
        listed.ShouldSatisfyAllConditions(c => c.Health.ShouldBe(CredentialHealth.Active, "derived from the row as the re-authorization left it: no sweep had to look first"), c => c.Status.ShouldBe(CredentialStatus.Active), c => c.LastError.ShouldBeNull(), c => c.DaysToExpiry.ShouldBeNull("an OAuth credential's access token renews itself"));

        (await LoadRepositoryAsync(repositoryId).ConfigureAwait(false)).ShouldSatisfyAllConditions(r => r.CredentialId.ShouldBe(credentialId), r => r.Status.ShouldBe(RepositoryStatus.Active));

        var stored = await StoredPairAsync(credentialId).ConfigureAwait(false);
        (await CredentialRefreshHarness.ResolveTokenAsync(_fixture, repositoryId).ConfigureAwait(false)).ShouldBe(stored.AccessToken, "a run bound through the repository resolves the new pair — the resolver no longer fails fast on it");
        gitlab.Count(StubEvent.Rotated).ShouldBe(0, "nothing refreshed: the pair the sign-in issued is fresh");
        gitlab.Count(StubEvent.Refused).ShouldBe(1, "nothing was refused after the re-authorization");
    }

    [Theory]
    [InlineData(CredentialStatus.Error, null, CredentialHealth.Active)]    // GitLab says the new token never expires
    [InlineData(CredentialStatus.Active, 90, CredentialHealth.Active)]     // still Active in the row, yet the list already read the lapsed token as rejected
    [InlineData(CredentialStatus.Error, 10, CredentialHealth.Expiring)]    // a new token that ends soon says so, as one that was added would
    public async Task Replacing_the_lapsed_token_of_a_credential_reads_as_the_new_token_does(CredentialStatus before, int? daysLeft, CredentialHealth expected)
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var lapsed = DateTimeOffset.UtcNow.AddDays(-3);
        var credentialId = await SeedTokenCredentialAsync(world, world.MayaId, "glpat-lapsed", status: before, lastError: before == CredentialStatus.Error ? "token rejected by GitLab" : null, expires: lapsed).ConfigureAwait(false);
        var repositoryId = await SeedRepositoryAsync(world, credentialId, RepositoryStatus.Active).ConfigureAwait(false);
        await SeedIdentityAsync(world, world.MayaId, credentialId, "maya").ConfigureAwait(false);
        var expiresAt = daysLeft is { } days ? DateTimeOffset.UtcNow.AddDays(days) : (DateTimeOffset?)null;
        gitlab.AddAccessToken("glpat-fresh", "maya", expiresAt);

        (await ListedAsync(world, world.MayaId, credentialId).ConfigureAwait(false)).Health.ShouldBe(CredentialHealth.Rejected, "fixture check: the lapsed token reads as refused whatever the row's status says");

        await ReplaceTokenAsync(world, world.MayaId, credentialId, "glpat-fresh").ConfigureAwait(false);

        var row = await LoadCredentialAsync(credentialId).ConfigureAwait(false);
        row.ShouldSatisfyAllConditions(r => r.Status.ShouldBe(CredentialStatus.Active), r => r.LastError.ShouldBeNull(), r => r.LastValidatedDate.ShouldNotBeNull().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-2)));
        DateTimeOffset? recorded = expiresAt is { } at ? new DateTimeOffset(at.UtcDateTime.Date, TimeSpan.Zero) : null;
        row.ExpiresDate.ShouldBe(recorded, "the new token's own expiry as GitLab reports it (a date), never the lapsed token's");

        var listed = await ListedAsync(world, world.MayaId, credentialId).ConfigureAwait(false);
        listed.Health.ShouldBe(expected);

        if (daysLeft is { } left) listed.DaysToExpiry.ShouldNotBeNull().ShouldBeInRange(left - 1, left);
        else listed.DaysToExpiry.ShouldBeNull();

        (await LoadRepositoryAsync(repositoryId).ConfigureAwait(false)).CredentialId.ShouldBe(credentialId);
        (await CredentialRefreshHarness.ResolveTokenAsync(_fixture, repositoryId).ConfigureAwait(false)).ShouldBe("glpat-fresh", "a run resolves the new token");
    }

    [Fact]
    public async Task A_pair_the_provider_refuses_at_its_first_use_is_marked_and_not_left_looking_active()
    {
        // With no account on record there is nothing to compare, so a pair the provider just issued is presumed good and is stored.
        // If the provider then refuses it for good — here a pair it never issued — the first thing to use it is the act-as relink
        // that follows the write, and that refusal is recorded like any other: the credential is Error, saying why, and the list
        // says rejected. A re-authorization does not get to leave a dead token reading Active.
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var credentialId = await SeedOAuthCredentialAsync(world, world.NachoId, "at-0", "rt-0").ConfigureAwait(false);

        await ReauthorizeInWorldAsync(world, credentialId, FreshPair("at-unknown")).ConfigureAwait(false);

        (await StoredPairAsync(credentialId).ConfigureAwait(false)).AccessToken.ShouldBe("at-unknown", "the pair was stored: nothing could say it was wrong when it was written");
        var listed = await ListedAsync(world, world.NachoId, credentialId).ConfigureAwait(false);
        listed.ShouldSatisfyAllConditions(c => c.Status.ShouldBe(CredentialStatus.Error), c => c.LastError.ShouldBe("needs reconnect: invalid_grant"), c => c.Health.ShouldBe(CredentialHealth.Rejected));
    }

    // ─── The connect-time rule for a refused credential's bindings is for a NEW row ──────────────────────────

    [Theory]
    [InlineData(Door.Reconnect)]
    [InlineData(Door.PastedToken)]
    public async Task Re_authorizing_one_refused_credential_in_place_moves_nothing_that_hangs_on_another_and_a_plain_connect_still_does(Door door)
    {
        // A plain connect treats every refused credential its owner holds as dead and carries its bindings to the new one. A
        // re-authorization creates no row, so there is nothing to carry them to: what hangs on a different refused credential
        // stays there until the person connects it again deliberately — which the second half shows is the connect that moves it.
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var target = door == Door.Reconnect
            ? await SeedOAuthCredentialAsync(world, world.NachoId, "at-dead", "rt-dead", status: CredentialStatus.Error, lastError: "needs reconnect: invalid_grant").ConfigureAwait(false)
            : await SeedTokenCredentialAsync(world, world.NachoId, "glpat-dead", status: CredentialStatus.Error, lastError: "token rejected by GitLab").ConfigureAwait(false);
        var other = await SeedOAuthCredentialAsync(world, world.NachoId, "at-9", "rt-9", status: CredentialStatus.Error, lastError: "needs reconnect: invalid_grant").ConfigureAwait(false);
        await SeedIdentityAsync(world, world.NachoId, target, "nacho").ConfigureAwait(false);
        var onTarget = await SeedRepositoryAsync(world, target, RepositoryStatus.Active).ConfigureAwait(false);
        var onOther = await SeedRepositoryAsync(world, other, RepositoryStatus.Active).ConfigureAwait(false);
        var otherHook = await SeedHookAsync(world, other, RepositoryWebhookRegistrationStatus.Failed).ConfigureAwait(false);

        await ReauthorizeAsNachoAsync(world, door, target, "nacho").ConfigureAwait(false);

        (await CountCredentialsAsync(world).ConfigureAwait(false)).ShouldBe(2, "no row was minted");
        (await LoadCredentialAsync(target).ConfigureAwait(false)).Status.ShouldBe(CredentialStatus.Active);
        (await LoadRepositoryAsync(onTarget).ConfigureAwait(false)).CredentialId.ShouldBe(target);
        (await LoadRepositoryAsync(onOther).ConfigureAwait(false)).CredentialId.ShouldBe(other, "the rule for a refused credential's bindings is a connect's, and this was not one");
        (await LoadHookAsync(otherHook).ConfigureAwait(false)).ShouldSatisfyAllConditions(h => h.CredentialId.ShouldBe(other), h => h.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Failed));
        (await LoadCredentialAsync(other).ConfigureAwait(false)).Status.ShouldBe(CredentialStatus.Error);

        var plain = await CompleteAsync(world, await InitAsync(world, world.NachoId, replaceCredentialId: null).ConfigureAwait(false), "authz:nacho").ConfigureAwait(false);

        plain.CredentialId.ShouldNotBe(target);
        (await LoadRepositoryAsync(onOther).ConfigureAwait(false)).CredentialId.ShouldBe(plain.CredentialId, "control: a plain connect does carry a refused credential's bindings to the new one");
        (await LoadRepositoryAsync(onTarget).ConfigureAwait(false)).CredentialId.ShouldBe(target, "the re-authorized credential is live again, so it has no successor");
    }

    // ─── A verdict about the replaced token cannot land on the new one ────────────────────────────────────────

    [Theory]
    [InlineData(false)]   // GitLab refuses the replaced token: the sweep's verdict is "rejected"
    [InlineData(true)]    // GitLab still accepts it, with an expiry soon: the sweep's verdict is "validated", with that expiry
    public async Task A_sweep_verdict_about_the_replaced_token_that_lands_after_the_replacement_is_not_written(bool oldTokenStillLive)
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var credentialId = await SeedTokenCredentialAsync(world, world.MayaId, "glpat-old").ConfigureAwait(false);
        await SeedIdentityAsync(world, world.MayaId, credentialId, "maya").ConfigureAwait(false);
        gitlab.AddAccessToken("glpat-old", "maya", oldTokenStillLive ? DateTimeOffset.UtcNow.AddDays(2) : null);
        if (!oldTokenStillLive) gitlab.RevokeAccessToken("glpat-old");
        var newExpiry = DateTimeOffset.UtcNow.AddDays(60);
        gitlab.AddAccessToken("glpat-new", "maya", newExpiry);
        await GitLabCredentialSeed.QuietEveryOtherCredentialAsync(_fixture, credentialId).ConfigureAwait(false);

        // The sweep has asked GitLab about glpat-old and holds a verdict on it when the person's replacement lands.
        var verdicts = new InterleavingHealthWriter(async () => await ReplaceTokenAsync(world, world.MayaId, credentialId, "glpat-new").ConfigureAwait(false));

        using (var scope = _fixture.BeginScope(b => b.Register(c => verdicts.Over(new CredentialHealthWriter(c.Resolve<CodeSpaceConnectionString>()))).As<ICredentialHealthWriter>().InstancePerLifetimeScope()))
            await scope.Resolve<IMediator>().Send(new ProbeCredentialsCommand { BatchSize = 25 }).ConfigureAwait(false);

        Dump(gitlab);
        var verdict = verdicts.Verdicts.ShouldHaveSingleItem("fixture check: the sweep reached a verdict on the credential and tried to write it");
        verdict.Kind.ShouldBe(oldTokenStillLive ? "validated" : "rejected");
        verdict.Landed.ShouldBeFalse("it judged a payload the row no longer holds");

        var row = await LoadCredentialAsync(credentialId).ConfigureAwait(false);
        row.ShouldSatisfyAllConditions(r => r.Status.ShouldBe(CredentialStatus.Active), r => r.LastError.ShouldBeNull(), r => r.ExpiresDate.ShouldBe(new DateTimeOffset(newExpiry.UtcDateTime.Date, TimeSpan.Zero), "the new token's expiry, not the replaced one's"));
        (await TokenOfAsync(credentialId).ConfigureAwait(false)).ShouldBe("glpat-new");
        (await ListedAsync(world, world.MayaId, credentialId).ConfigureAwait(false)).Health.ShouldBe(CredentialHealth.Active);
    }

    [Fact]
    public async Task A_refresh_waiting_behind_a_re_authorization_does_not_present_the_dead_grant_and_does_not_mark_the_new_pair_error()
    {
        // The row holds a grant GitLab has ended. A refresh that ran now would be refused for good and would mark the credential
        // Error — over the very row the person is re-authorizing. It takes the lock the re-authorization holds, so it waits, then
        // finds the new pair fresh and has nothing to refresh.
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var credentialId = await SeedOAuthCredentialAsync(world, world.NachoId, "at-dead", "rt-dead", expired: true).ConfigureAwait(false);
        var repositoryId = await SeedRepositoryAsync(world, credentialId, RepositoryStatus.Active).ConfigureAwait(false);
        await SeedIdentityAsync(world, world.NachoId, credentialId, "nacho").ConfigureAwait(false);
        var writer = new GatedReauthorizationWriter();
        var init = await InitAsync(world, world.NachoId, credentialId).ConfigureAwait(false);

        var reconnect = Task.Run(() => CompleteAsync(world, init, "authz:nacho", b => b.Register(c => writer.Over(new CredentialReauthorizationWriter(c.Resolve<CodeSpaceConnectionString>()))).As<ICredentialReauthorizationWriter>().InstancePerLifetimeScope()));
        await WithinPatienceAsync(writer.Entered, "the re-authorization reached its write", credentialId).ConfigureAwait(false);

        var run = Task.Run(() => CredentialRefreshHarness.ResolveTokenAsync(_fixture, repositoryId));
        await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        run.IsCompleted.ShouldBeFalse("the refresh waits for the lock the re-authorization holds");
        gitlab.Count(StubEvent.Refused).ShouldBe(0, "the dead refresh token has not been presented to GitLab");

        writer.Release();
        await WithinPatienceAsync(reconnect, "the re-authorization finished once released", credentialId).ConfigureAwait(false);
        var token = await WithinPatienceAsync(run, "the run that waited for it finished", credentialId).ConfigureAwait(false);

        Dump(gitlab);
        var stored = await StoredPairAsync(credentialId).ConfigureAwait(false);
        token.ShouldBe(stored.AccessToken, "the run read the row after the lock and found the new pair");
        gitlab.Count(StubEvent.Refused).ShouldBe(0, "no refusal was ever recorded against the credential, so none could mark it");
        (await LoadCredentialAsync(credentialId).ConfigureAwait(false)).ShouldSatisfyAllConditions(r => r.Status.ShouldBe(CredentialStatus.Active), r => r.LastError.ShouldBeNull());
        (await ListedAsync(world, world.NachoId, credentialId).ConfigureAwait(false)).Health.ShouldBe(CredentialHealth.Active);
    }

    [Fact]
    public async Task A_pair_this_process_kept_for_the_replaced_grant_is_dropped_and_never_stored_over_the_new_authorization()
    {
        // GitLab rotated rt-0 and every store failed, so this process keeps the pair it was issued, to store it on the credential's
        // next use. The person then re-authorizes. A kept pair that survived would, on that next use, find the row changed and settle
        // it by which pair expires later — and a new authorization whose tokens live shorter would lose to the pair of the grant it
        // replaced. The re-authorization drops the kept pair under the lock it already holds.
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        gitlab.Seed(GitLabDoorkeeperStub.DefaultClientId, "nacho", "at-0", "rt-0");
        var credentialId = await SeedOAuthCredentialAsync(world, world.NachoId, "at-0", "rt-0", expired: true).ConfigureAwait(false);
        var repositoryId = await SeedRepositoryAsync(world, credentialId, RepositoryStatus.Active).ConfigureAwait(false);
        await SeedIdentityAsync(world, world.NachoId, credentialId, "nacho").ConfigureAwait(false);

        await Should.ThrowAsync<NpgsqlException>(() => CredentialRefreshHarness.ResolveTokenAsync(_fixture, repositoryId, inner => new FailingCredentialPayloadWriter(inner, int.MaxValue))).ConfigureAwait(false);

        gitlab.Count(StubEvent.Rotated).ShouldBe(1, "fixture check: GitLab rotated rt-0 and this process kept the pair it issued, a two-hour one");
        gitlab.ExpiresInSeconds = 900;

        await ReconnectAsync(world, world.NachoId, credentialId, "authz:nacho").ConfigureAwait(false);

        var reauthorized = await StoredPairAsync(credentialId).ConfigureAwait(false);
        reauthorized.ExpiresAt.ShouldNotBeNull().ShouldBeLessThan(DateTimeOffset.UtcNow.AddMinutes(20), "fixture check: the new authorization's pair lives for fifteen minutes, shorter than the kept pair's two hours");

        var token = await CredentialRefreshHarness.ResolveTokenAsync(_fixture, repositoryId).ConfigureAwait(false);

        Dump(gitlab);
        token.ShouldBe(reauthorized.AccessToken, "the next run uses the new authorization, not the pair kept for the grant it replaced");
        (await StoredPairAsync(credentialId).ConfigureAwait(false)).AccessToken.ShouldBe(reauthorized.AccessToken, "and the kept pair was not stored over it");
        gitlab.Count(StubEvent.Rotated).ShouldBe(1, "nothing was refreshed after the re-authorization");
    }

    // ─── The provider that stops answering ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_provider_that_never_answers_costs_a_pasted_token_its_probe_deadline_and_changes_nothing()
    {
        // Asking about the new token is bounded as the sweep's and the add's probes are: a provider that accepts the connection and
        // never replies costs this request the probe deadline, not a thread for the HTTP client's five minutes and three attempts.
        using var gitlab = new GitLabDoorkeeperStub();
        using var silent = new SilentProviderHost();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var silentInstance = await SeedInstanceAtAsync(world, silent.BaseUrl).ConfigureAwait(false);
        var credentialId = await SeedTokenCredentialAsync(world, world.MayaId, "glpat-dead", status: CredentialStatus.Error, lastError: "token rejected by GitLab", instanceId: silentInstance).ConfigureAwait(false);
        var before = await LoadCredentialAsync(credentialId).ConfigureAwait(false);
        using var scope = Scope(world, world.MayaId, b => b.RegisterInstance(new CredentialHealthDeadlines(Probe: TimeSpan.FromSeconds(1), Pass: TimeSpan.FromMinutes(1))).SingleInstance());

        var replace = Task.Run(() => scope.Resolve<IMediator>().Send(new ReplaceCredentialTokenCommand { CredentialId = credentialId, Token = "glpat-fresh" }));
        var finished = await Task.WhenAny(replace, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false);

        finished.ShouldBe(replace, "the replacement was still waiting on a silent provider after 30 seconds — nothing bounded it");
        var refusal = await Should.ThrowAsync<InvalidOperationException>(() => replace).ConfigureAwait(false);

        refusal.Message.ShouldContain("Could not validate the token against GitLab");
        refusal.Message.ShouldContain("did not answer within");
        silent.Connections.ShouldBe(1, "a call given up on at its deadline is not sent again");
        var after = await LoadCredentialAsync(credentialId).ConfigureAwait(false);
        after.EncryptedPayload.ShouldBe(before.EncryptedPayload, "no answer is not an acceptance: the row was not touched");
        after.ShouldSatisfyAllConditions(c => c.Status.ShouldBe(CredentialStatus.Error), c => c.LastError.ShouldBe("token rejected by GitLab"));
    }

    // ─── Plumbing ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The credential as the Providers list reads it, with the health derived from its row.</summary>
    private async Task<CredentialSummary> ListedAsync(World world, Guid userId, Guid credentialId)
    {
        using var scope = Scope(world, userId);
        var listed = await scope.Resolve<ICredentialService>().ListAsync(null, CancellationToken.None).ConfigureAwait(false);

        return listed.Single(c => c.Id == credentialId);
    }

    /// <summary>
    /// Stands between the sweep and the credential row: the first verdict it is handed is held until <c>beforeFirstVerdict</c>
    /// has run — the person's replacement landing after GitLab answered about the old token and before the answer is written —
    /// then goes through to the real writer, whose compare-and-swap is what decides it. Records each verdict and whether it landed.
    /// </summary>
    private sealed class InterleavingHealthWriter : ICredentialHealthWriter
    {
        private readonly Func<Task> _beforeFirstVerdict;
        private readonly List<Verdict> _verdicts = new();
        private ICredentialHealthWriter _inner = default!;
        private int _first;

        public InterleavingHealthWriter(Func<Task> beforeFirstVerdict) { _beforeFirstVerdict = beforeFirstVerdict; }

        public IReadOnlyList<Verdict> Verdicts { get { lock (_verdicts) { return _verdicts.ToList(); } } }

        public InterleavingHealthWriter Over(ICredentialHealthWriter inner)
        {
            _inner = inner;

            return this;
        }

        public async Task<bool> MarkRejectedAsync(Guid credentialId, string expectedEncryptedPayload, string lastError, CancellationToken cancellationToken)
        {
            await InterleaveOnceAsync().ConfigureAwait(false);

            return Record("rejected", await _inner.MarkRejectedAsync(credentialId, expectedEncryptedPayload, lastError, cancellationToken).ConfigureAwait(false));
        }

        public async Task<bool> MarkValidatedAsync(Guid credentialId, string expectedEncryptedPayload, DateTimeOffset validatedAt, ExpiryRecord? expiry, CancellationToken cancellationToken)
        {
            await InterleaveOnceAsync().ConfigureAwait(false);

            return Record("validated", await _inner.MarkValidatedAsync(credentialId, expectedEncryptedPayload, validatedAt, expiry, cancellationToken).ConfigureAwait(false));
        }

        private async Task InterleaveOnceAsync()
        {
            if (Interlocked.Increment(ref _first) == 1) await _beforeFirstVerdict().ConfigureAwait(false);
        }

        private bool Record(string kind, bool landed)
        {
            lock (_verdicts) _verdicts.Add(new Verdict(kind, landed));

            return landed;
        }

        public sealed record Verdict(string Kind, bool Landed);
    }
}
