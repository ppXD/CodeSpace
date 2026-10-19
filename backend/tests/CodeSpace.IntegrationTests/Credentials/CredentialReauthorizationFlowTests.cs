using Autofac;
using CodeSpace.Core.Authorization;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Core.Services.Webhooks.Registration;
using CodeSpace.Core.Settings.Database;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.OAuth;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using CodeSpace.Messages.Failures;
using CodeSpace.StorageTestWorker;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using Xunit.Abstractions;

namespace CodeSpace.IntegrationTests.Credentials;

/// <summary>
/// 🟢 High fidelity — a credential's token re-authorized IN PLACE, through the real mediator pipeline (the authorization
/// behaviors, the unit of work, the post-commit drain), the real <c>OAuthFlowService</c> and <c>CredentialReauthorizer</c>,
/// the real GitLab OAuth client and GitLab provider (the probe that says whose account signed in), the real refresher and
/// its Postgres advisory lock, against real Postgres. The one fake is GitLab, a <see cref="GitLabDoorkeeperStub"/>: refresh
/// tokens are single use, a refresh kills the access token it replaces, an authorization code <c>authz:&lt;user&gt;</c> issues
/// that user a pair, and every user has an id of their own.
///
/// <para>The claim: a reconnect used to mint a NEW credential row, so everything bound through the old one — repositories,
/// group hooks, the act-as identity — pointed at a dead row until a succession subsystem moved it. Re-authorization writes the
/// new token into the SAME row, so nothing moves and nothing can be left behind: the credential id never changes, what the
/// dead token broke comes back, the replaced tokens are ended at the provider (those tokens, never the user's grant), another
/// account is refused, and a refresh that races it is serialised by the very lock the refresher takes.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public partial class CredentialReauthorizationFlowTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly PostgresFixture _fixture;
    private readonly ITestOutputHelper _output;

    public CredentialReauthorizationFlowTests(PostgresFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    // ─── The OAuth door: Reconnect ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reconnecting_a_disconnected_credential_keeps_its_id_and_brings_back_everything_its_dead_token_broke()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var credentialId = await SeedOAuthCredentialAsync(world, world.NachoId, "at-0", "rt-0", disconnected: true).ConfigureAwait(false);
        var broken = await SeedRepositoryAsync(world, credentialId, RepositoryStatus.Error).ConfigureAwait(false);
        var healthy = await SeedRepositoryAsync(world, credentialId, RepositoryStatus.Active).ConfigureAwait(false);
        var paused = await SeedRepositoryAsync(world, credentialId, RepositoryStatus.Paused).ConfigureAwait(false);
        var failedHook = await SeedHookAsync(world, credentialId, RepositoryWebhookRegistrationStatus.Failed).ConfigureAwait(false);
        var buriedHook = await SeedHookAsync(world, credentialId, RepositoryWebhookRegistrationStatus.DeadLettered).ConfigureAwait(false);
        var registeredHook = await SeedHookAsync(world, credentialId, RepositoryWebhookRegistrationStatus.Registered).ConfigureAwait(false);
        await SeedIdentityAsync(world, world.NachoId, credentialId, "nacho", deleted: true).ConfigureAwait(false);

        var finished = await ReconnectAsync(world, world.NachoId, credentialId, "authz:nacho").ConfigureAwait(false);

        finished.CredentialId.ShouldBe(credentialId, "the credential keeps its id");
        (await CountCredentialsAsync(world).ConfigureAwait(false)).ShouldBe(1, "no second row was minted");

        var row = await LoadCredentialAsync(credentialId).ConfigureAwait(false);
        row.Status.ShouldBe(CredentialStatus.Active);
        row.LastError.ShouldBeNull();
        row.LastValidatedDate.ShouldNotBeNull().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-2));
        row.DeletedDate.ShouldBeNull();
        row.AuthType.ShouldBe(AuthType.OAuth);
        row.ExpiresDate.ShouldNotBeNull().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddHours(1));
        row.LastModifiedBy.ShouldBe(world.NachoId, "attributed to the person who reconnected, not to the anonymous callback");
        (await StoredPairAsync(credentialId).ConfigureAwait(false)).RefreshToken.ShouldBe(gitlab.LiveRefreshTokenOf("nacho"), "the row holds the pair GitLab issued for this sign-in");

        (await LoadRepositoryAsync(broken).ConfigureAwait(false)).ShouldSatisfyAllConditions(r => r.CredentialId.ShouldBe(credentialId), r => r.Status.ShouldBe(RepositoryStatus.Active), r => r.LastError.ShouldBeNull());
        (await LoadRepositoryAsync(healthy).ConfigureAwait(false)).CredentialId.ShouldBe(credentialId);
        (await LoadRepositoryAsync(paused).ConfigureAwait(false)).ShouldSatisfyAllConditions(r => r.CredentialId.ShouldBe(credentialId), r => r.Status.ShouldBe(RepositoryStatus.Paused, "a repository paused for its own reasons stays paused"));

        foreach (var hookId in new[] { failedHook, buriedHook })
            (await LoadHookAsync(hookId).ConfigureAwait(false)).ShouldSatisfyAllConditions(h => h.CredentialId.ShouldBe(credentialId), h => h.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Enqueued, "revived to Pending, then handed to the registrar once the unit of work committed"), h => h.Attempts.ShouldBe(0), h => WasDispatched(h.Id).ShouldBeTrue());

        (await LoadHookAsync(registeredHook).ConfigureAwait(false)).RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Registered, "a hook that works only needs a live credential for its next call");

        var identity = (await LoadIdentitiesAsync(world.NachoId).ConfigureAwait(false)).Single(i => i.DeletedDate == null);
        identity.CredentialId.ShouldBe(credentialId, "the Disconnect ended the owner's act-as identity; reconnecting links it again, to the same credential");
        identity.ProviderUserId.ShouldBe(gitlab.UserIdOf("nacho").ToString());
    }

    [Fact]
    public async Task Reconnecting_a_failing_credential_replaces_its_pair_and_ends_only_the_tokens_it_replaced()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        gitlab.Seed(GitLabDoorkeeperStub.DefaultClientId, "nacho", "at-0", "rt-0");
        var credentialId = await SeedOAuthCredentialAsync(world, world.NachoId, "at-0", "rt-0", status: CredentialStatus.Error, lastError: "OAuth exchange rejected: invalid_grant").ConfigureAwait(false);
        var repositoryId = await SeedRepositoryAsync(world, credentialId, RepositoryStatus.Active).ConfigureAwait(false);
        await SeedIdentityAsync(world, world.NachoId, credentialId, "nacho").ConfigureAwait(false);

        await ReconnectAsync(world, world.NachoId, credentialId, "authz:nacho").ConfigureAwait(false);

        Dump(gitlab);
        var row = await LoadCredentialAsync(credentialId).ConfigureAwait(false);
        row.Status.ShouldBe(CredentialStatus.Active);
        row.LastError.ShouldBeNull("the failure the row carried is cleared by a sign-in that works");

        var stored = await StoredPairAsync(credentialId).ConfigureAwait(false);
        stored.AccessToken.ShouldNotBe("at-0");
        gitlab.IsAccessTokenLive(stored.AccessToken).ShouldBeTrue("the new pair is the live one");
        gitlab.IsAccessTokenLive("at-0").ShouldBeFalse("the pair it replaced is ended at the provider");
        gitlab.IsRefreshTokenLive("rt-0").ShouldBeFalse();
        gitlab.Count(StubEvent.Revoked).ShouldBe(2, "the replaced refresh token and the replaced access token — and nothing else");
        gitlab.Count(StubEvent.Refused).ShouldBe(0);
        (await LoadRepositoryAsync(repositoryId).ConfigureAwait(false)).CredentialId.ShouldBe(credentialId);
        (await CountCredentialsAsync(world).ConfigureAwait(false)).ShouldBe(1);
        (await CredentialRefreshHarness.ResolveTokenAsync(_fixture, repositoryId).ConfigureAwait(false)).ShouldBe(stored.AccessToken, "a run bound through the same repository resolves the new token, with no refresh");
    }

    [Fact]
    public async Task The_tokens_a_reconnect_replaces_are_revoked_at_token_level_and_never_as_a_whole_grant()
    {
        // On GitHub a grant revoke ends every token the user holds under the OAuth App — the new one included, and the user's
        // tokens in every other team connected through the same app — so a re-authorization must only ever end its own.
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var credentialId = await SeedOAuthCredentialAsync(world, world.NachoId, "at-0", "rt-0").ConfigureAwait(false);
        await SeedIdentityAsync(world, world.NachoId, credentialId, "nacho").ConfigureAwait(false);
        var revoker = new RecordingGrantRevoker(() => StoredPairAsync(credentialId).GetAwaiter().GetResult().AccessToken);

        await ReconnectAsync(world, world.NachoId, credentialId, "authz:nacho", b => b.RegisterInstance(revoker).As<IOAuthGrantRevoker>()).ConfigureAwait(false);

        var revoked = revoker.Revocations.ShouldHaveSingleItem();
        revoked.WholeGrant.ShouldBeFalse("only the replaced tokens may be ended");
        revoked.Payload.AccessToken.ShouldBe("at-0");
        revoked.Payload.RefreshToken.ShouldBe("rt-0");
        revoked.StoredAccessTokenWhenAsked.ShouldBe((await StoredPairAsync(credentialId).ConfigureAwait(false)).AccessToken, "asked only once the new pair is stored and the unit of work committed");
    }

    [Fact]
    public async Task A_provider_that_fails_to_revoke_the_replaced_tokens_does_not_undo_the_reconnect()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var credentialId = await SeedOAuthCredentialAsync(world, world.NachoId, "at-0", "rt-0").ConfigureAwait(false);
        var repositoryId = await SeedRepositoryAsync(world, credentialId, RepositoryStatus.Error).ConfigureAwait(false);

        await ReconnectAsync(world, world.NachoId, credentialId, "authz:nacho", b => b.RegisterInstance(new RecordingGrantRevoker { Fails = true }).As<IOAuthGrantRevoker>()).ConfigureAwait(false);

        (await LoadCredentialAsync(credentialId).ConfigureAwait(false)).Status.ShouldBe(CredentialStatus.Active);
        (await LoadRepositoryAsync(repositoryId).ConfigureAwait(false)).Status.ShouldBe(RepositoryStatus.Active);
    }

    [Fact]
    public async Task A_reconnect_does_not_carry_other_credentials_bindings_onto_the_one_it_re_authorizes()
    {
        // A plain connect claims every binding of the owner's disconnected credentials. Re-authorizing one credential is not a
        // new connection: what hangs on a different dead credential stays there, for whoever relinks it deliberately.
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var credentialId = await SeedOAuthCredentialAsync(world, world.NachoId, "at-0", "rt-0", disconnected: true).ConfigureAwait(false);
        var other = await SeedOAuthCredentialAsync(world, world.NachoId, "at-9", "rt-9", disconnected: true).ConfigureAwait(false);
        var strandedOnOther = await SeedRepositoryAsync(world, other, RepositoryStatus.Error).ConfigureAwait(false);

        await ReconnectAsync(world, world.NachoId, credentialId, "authz:nacho").ConfigureAwait(false);

        (await LoadRepositoryAsync(strandedOnOther).ConfigureAwait(false)).ShouldSatisfyAllConditions(r => r.CredentialId.ShouldBe(other), r => r.Status.ShouldBe(RepositoryStatus.Error));
    }

    [Fact]
    public async Task A_different_provider_account_is_refused_and_nothing_changes()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var credentialId = await SeedOAuthCredentialAsync(world, world.NachoId, "at-0", "rt-0", disconnected: true).ConfigureAwait(false);
        var repositoryId = await SeedRepositoryAsync(world, credentialId, RepositoryStatus.Error).ConfigureAwait(false);
        var hookId = await SeedHookAsync(world, credentialId, RepositoryWebhookRegistrationStatus.Failed).ConfigureAwait(false);
        await SeedIdentityAsync(world, world.NachoId, credentialId, "nacho", deleted: true).ConfigureAwait(false);
        gitlab.UserIdOf("nacho");
        var before = await LoadCredentialAsync(credentialId).ConfigureAwait(false);

        var refusal = await Should.ThrowAsync<CredentialAccountMismatchException>(() => ReconnectAsync(world, world.NachoId, credentialId, "authz:someone-else")).ConfigureAwait(false);

        refusal.Code.ShouldBe(FailureCodes.CredentialAccountMismatch);
        refusal.ExpectedAccount.ShouldBe("nacho");
        refusal.PresentedAccount.ShouldBe("someone-else");
        refusal.Message.ShouldContain("connect someone-else as its own credential", Case.Insensitive, "the message says what to do instead");

        var after = await LoadCredentialAsync(credentialId).ConfigureAwait(false);
        after.EncryptedPayload.ShouldBe(before.EncryptedPayload, "the row was not touched");
        after.Status.ShouldBe(CredentialStatus.Revoked);
        (await LoadRepositoryAsync(repositoryId).ConfigureAwait(false)).Status.ShouldBe(RepositoryStatus.Error);
        (await LoadHookAsync(hookId).ConfigureAwait(false)).RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Failed);
        (await CountCredentialsAsync(world).ConfigureAwait(false)).ShouldBe(1, "a different account is a new connection, which this is not");
        (await LoadIdentitiesAsync(world.NachoId).ConfigureAwait(false)).ShouldAllBe(i => i.DeletedDate != null, "no identity was linked for the other account");
    }

    [Fact]
    public async Task A_credential_with_no_account_on_record_takes_the_first_one_and_records_it()
    {
        // Nothing to compare: the credential was connected before identities were linked, or its identity link failed. Refusing
        // would make a dead credential like that impossible to bring back; the identity linked now is what records the account.
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var credentialId = await SeedOAuthCredentialAsync(world, world.NachoId, "at-0", "rt-0", disconnected: true).ConfigureAwait(false);

        await ReconnectAsync(world, world.NachoId, credentialId, "authz:nacho").ConfigureAwait(false);

        var identity = (await LoadIdentitiesAsync(world.NachoId).ConfigureAwait(false)).ShouldHaveSingleItem();
        identity.ShouldSatisfyAllConditions(i => i.CredentialId.ShouldBe(credentialId), i => i.ProviderUsername.ShouldBe("nacho"), i => i.DeletedDate.ShouldBeNull());

        var refusal = await Should.ThrowAsync<CredentialAccountMismatchException>(() => ReconnectAsync(world, world.NachoId, credentialId, "authz:someone-else")).ConfigureAwait(false);
        refusal.ExpectedAccount.ShouldBe("nacho", "from now on the credential has an account on record");
    }

    [Fact]
    public async Task A_teammate_with_authority_over_a_credential_re_authorizes_it_without_taking_over_its_identity()
    {
        // An Admin reconnects Maya's credential: the account that signs in must still be Maya's, and the act-as identity is
        // linked only by the person it speaks for — Maya — never by whoever happened to click.
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var credentialId = await SeedOAuthCredentialAsync(world, world.MayaId, "at-0", "rt-0", disconnected: true).ConfigureAwait(false);
        await SeedIdentityAsync(world, world.MayaId, credentialId, "maya", deleted: true).ConfigureAwait(false);

        await Should.ThrowAsync<CredentialAccountMismatchException>(() => ReconnectAsync(world, world.BossId, credentialId, "authz:boss")).ConfigureAwait(false);

        var finished = await ReconnectAsync(world, world.BossId, credentialId, "authz:maya").ConfigureAwait(false);

        finished.CredentialId.ShouldBe(credentialId);
        var row = await LoadCredentialAsync(credentialId).ConfigureAwait(false);
        row.OwnerUserId.ShouldBe(world.MayaId, "the credential is still Maya's");
        row.LastModifiedBy.ShouldBe(world.BossId);
        (await LoadIdentitiesAsync(world.MayaId).ConfigureAwait(false)).ShouldAllBe(i => i.DeletedDate != null, "Maya did not sign in, so nobody spoke for her");
    }

    // ─── Refusals at the start and at the end of a connect ───────────────────────────────────────────────────

    [Fact]
    public async Task A_credential_retired_as_a_departed_members_is_refused_at_the_start_and_stays_retired()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var credentialId = await SeedOAuthCredentialAsync(world, world.NachoId, "at-0", "rt-0", disconnected: true, retired: true).ConfigureAwait(false);

        var refusal = await Should.ThrowAsync<CredentialRetiredException>(() => InitAsync(world, world.NachoId, credentialId)).ConfigureAwait(false);

        refusal.Code.ShouldBe(FailureCodes.CredentialRetired);
        (await CountPendingStatesAsync(world).ConfigureAwait(false)).ShouldBe(0, "a refused start leaves no pending connect behind");
    }

    [Fact]
    public async Task A_credential_retired_while_the_connect_was_pending_is_refused_at_the_callback_and_not_brought_back()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var credentialId = await SeedOAuthCredentialAsync(world, world.NachoId, "at-0", "rt-0").ConfigureAwait(false);
        var init = await InitAsync(world, world.NachoId, credentialId).ConfigureAwait(false);

        await RetireAsync(credentialId).ConfigureAwait(false);

        await Should.ThrowAsync<CredentialRetiredException>(() => CompleteAsync(world, init, "authz:nacho")).ConfigureAwait(false);

        var row = await LoadCredentialAsync(credentialId).ConfigureAwait(false);
        row.EncryptedPayload.ShouldBeEmpty("a retired row keeps no token");
        row.DeletedDate.ShouldNotBeNull();
        row.Status.ShouldBe(CredentialStatus.Revoked);
    }

    [Fact]
    public async Task A_credential_whose_person_no_longer_stands_in_the_team_is_refused_before_the_sweep_gets_to_it()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var credentialId = await SeedOAuthCredentialAsync(world, world.MayaId, "at-0", "rt-0").ConfigureAwait(false);
        var init = await InitAsync(world, world.BossId, credentialId).ConfigureAwait(false);

        await RemoveMembershipAsync(world, world.MayaId).ConfigureAwait(false);

        await Should.ThrowAsync<OAuthCallbackException>(() => CompleteAsync(world, init, "authz:maya")).ConfigureAwait(false);

        (await LoadCredentialAsync(credentialId).ConfigureAwait(false)).EncryptedPayload.ShouldNotBeEmpty("it is the sweep's to retire, and this did not resurrect or rewrite it");
        gitlab.Count(StubEvent.Authorized).ShouldBe(0, "the owner's standing is asked before the code is exchanged, so no token was minted for someone without standing");
    }

    [Fact]
    public async Task A_credential_of_another_team_reads_as_not_found_and_no_state_is_created()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var elsewhere = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var foreign = await SeedOAuthCredentialAsync(elsewhere, elsewhere.NachoId, "at-f", "rt-f").ConfigureAwait(false);

        await Should.ThrowAsync<KeyNotFoundException>(() => InitAsync(world, world.NachoId, foreign)).ConfigureAwait(false);

        (await CountPendingStatesAsync(world).ConfigureAwait(false)).ShouldBe(0);
        (await StoredPairAsync(foreign).ConfigureAwait(false)).AccessToken.ShouldBe("at-f");
    }

    [Fact]
    public async Task A_credential_of_another_provider_instance_a_team_service_credential_and_a_name_that_is_not_the_owner_are_refused_at_the_start()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var otherInstance = await SeedInstanceAsync(world, gitlab, clientId: "another-app").ConfigureAwait(false);
        var onOtherInstance = await SeedOAuthCredentialAsync(world, world.NachoId, "at-o", "rt-o", instanceId: otherInstance).ConfigureAwait(false);
        var teamToken = await SeedOAuthCredentialAsync(world, null, "at-t", "rt-t", ownership: CredentialOwnership.TeamService).ConfigureAwait(false);
        var teamTokenWithAnOwnerRecorded = await SeedOAuthCredentialAsync(world, world.NachoId, "at-u", "rt-u", ownership: CredentialOwnership.TeamService).ConfigureAwait(false);
        var nachos = await SeedOAuthCredentialAsync(world, world.NachoId, "at-n", "rt-n").ConfigureAwait(false);

        await Should.ThrowAsync<ArgumentException>(() => InitAsync(world, world.NachoId, onOtherInstance)).ConfigureAwait(false);
        await Should.ThrowAsync<ArgumentException>(() => InitAsync(world, world.NachoId, teamToken)).ConfigureAwait(false);
        await Should.ThrowAsync<ArgumentException>(() => InitAsync(world, world.NachoId, teamTokenWithAnOwnerRecorded)).ConfigureAwait(false);
        await Should.ThrowAsync<ArgumentException>(() => InitAsync(world, world.NachoId, nachos, intendedOwnerUserId: world.MayaId)).ConfigureAwait(false);

        (await CountPendingStatesAsync(world).ConfigureAwait(false)).ShouldBe(0);
    }

    [Fact]
    public async Task Starting_a_reconnect_takes_credentials_manage_unless_the_credential_is_the_askers_own()
    {
        // The start of any OAuth connect is the Admin's (the marker on the command); naming a credential lowers it to the credential's
        // own person — and to no one else, whatever their role (the owner's own start is pinned in the OwnerReconnect tests).
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var mayas = await SeedOAuthCredentialAsync(world, world.MayaId, "at-m", "rt-m").ConfigureAwait(false);

        await Should.ThrowAsync<TenantAccessDeniedException>(() => InitAsync(world, world.ViewerId, mayas)).ConfigureAwait(false);

        (await CountPendingStatesAsync(world).ConfigureAwait(false)).ShouldBe(0);
    }

    // ─── Mixed versions ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_pending_connect_an_older_pod_wrote_without_the_column_completes_as_a_new_credential_exactly_as_it_always_did()
    {
        // A pod from before migration 0250 inserts the row with the columns it knows — no replace_credential_id — so the
        // column is NULL, which is a plain connect for every version. Written as that pod writes it, not through this model.
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var existing = await SeedOAuthCredentialAsync(world, world.NachoId, "at-0", "rt-0", disconnected: true).ConfigureAwait(false);
        var repositoryId = await SeedRepositoryAsync(world, existing, RepositoryStatus.Error).ConfigureAwait(false);
        var nonce = OAuthBrowserNonce.Mint();
        var state = $"old-pod-{Guid.NewGuid():N}";

        await InsertPreMigrationStateAsync(world, state, world.NachoId, OAuthBrowserNonce.Hash(nonce)).ConfigureAwait(false);

        var finished = await CompleteAsync(world, new InitCredentialOAuthResult { AuthorizeUrl = new Uri("https://unused.test/"), State = state, BrowserNonce = nonce, Lifetime = TimeSpan.FromMinutes(10), BrowserNonceCookiePath = "/" }, "authz:nacho").ConfigureAwait(false);

        finished.CredentialId.ShouldNotBe(existing, "no replace id, so a connect mints a new credential");
        (await CountCredentialsAsync(world).ConfigureAwait(false)).ShouldBe(2);
        (await LoadRepositoryAsync(repositoryId).ConfigureAwait(false)).CredentialId.ShouldBe(finished.CredentialId, "and the succession that always followed a connect moved the repository onto it");
    }

    [Fact]
    public async Task A_connect_that_names_no_credential_is_the_connect_it_always_was()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var existing = await SeedOAuthCredentialAsync(world, world.NachoId, "at-0", "rt-0", disconnected: true).ConfigureAwait(false);
        var repositoryId = await SeedRepositoryAsync(world, existing, RepositoryStatus.Error).ConfigureAwait(false);

        var init = await InitAsync(world, world.NachoId, replaceCredentialId: null).ConfigureAwait(false);
        (await LoadPendingStateAsync(init.State).ConfigureAwait(false)).ReplaceCredentialId.ShouldBeNull();

        var finished = await CompleteAsync(world, init, "authz:nacho").ConfigureAwait(false);

        finished.CredentialId.ShouldNotBe(existing);
        (await LoadRepositoryAsync(repositoryId).ConfigureAwait(false)).CredentialId.ShouldBe(finished.CredentialId);
    }

    // ─── The pasted-token door: Replace token ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Replacing_a_token_keeps_the_credential_and_brings_back_what_its_dead_token_broke()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var credentialId = await SeedTokenCredentialAsync(world, world.MayaId, "glpat-expired", status: CredentialStatus.Error, lastError: "401 Unauthorized").ConfigureAwait(false);
        var repositoryId = await SeedRepositoryAsync(world, credentialId, RepositoryStatus.Error).ConfigureAwait(false);
        var hookId = await SeedHookAsync(world, credentialId, RepositoryWebhookRegistrationStatus.DeadLettered).ConfigureAwait(false);
        await SeedIdentityAsync(world, world.MayaId, credentialId, "maya").ConfigureAwait(false);
        gitlab.SeedPersonalAccessToken("maya", "glpat-fresh");

        var result = await ReplaceTokenAsync(world, world.MayaId, credentialId, "glpat-fresh").ConfigureAwait(false);

        result.CredentialId.ShouldBe(credentialId);
        result.ProviderUsername.ShouldBe("maya");
        var row = await LoadCredentialAsync(credentialId).ConfigureAwait(false);
        row.ShouldSatisfyAllConditions(r => r.Status.ShouldBe(CredentialStatus.Active), r => r.LastError.ShouldBeNull(), r => r.AuthType.ShouldBe(AuthType.Pat), r => r.ExpiresDate.ShouldBeNull(), r => r.LastValidatedDate.ShouldNotBeNull(), r => r.LastModifiedBy.ShouldBe(world.MayaId));
        row.Scopes.ShouldBe(GitLabDoorkeeperStub.PersonalAccessTokenScopes.Split(','), ignoreOrder: false, customMessage: "the token's real scopes, as the provider reports them");
        (await TokenOfAsync(credentialId).ConfigureAwait(false)).ShouldBe("glpat-fresh");
        (await LoadRepositoryAsync(repositoryId).ConfigureAwait(false)).ShouldSatisfyAllConditions(r => r.CredentialId.ShouldBe(credentialId), r => r.Status.ShouldBe(RepositoryStatus.Active));
        (await LoadHookAsync(hookId).ConfigureAwait(false)).RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Enqueued);
        (await CountCredentialsAsync(world).ConfigureAwait(false)).ShouldBe(1);
        gitlab.Count(StubEvent.Revoked).ShouldBe(0, "a personal access token is not something CodeSpace can revoke at the provider");
    }

    [Fact]
    public async Task A_token_replaces_an_oauth_sign_in_in_place_and_the_replaced_oauth_tokens_are_ended()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        gitlab.Seed(GitLabDoorkeeperStub.DefaultClientId, "maya", "at-0", "rt-0");
        var credentialId = await SeedOAuthCredentialAsync(world, world.MayaId, "at-0", "rt-0").ConfigureAwait(false);
        await SeedIdentityAsync(world, world.MayaId, credentialId, "maya").ConfigureAwait(false);
        gitlab.SeedPersonalAccessToken("maya", "glpat-fresh");

        await ReplaceTokenAsync(world, world.MayaId, credentialId, "glpat-fresh").ConfigureAwait(false);

        var row = await LoadCredentialAsync(credentialId).ConfigureAwait(false);
        row.AuthType.ShouldBe(AuthType.Pat, "bindings reference only the id, so the kind of token behind it may change");
        row.ExpiresDate.ShouldBeNull("an OAuth expiry must not outlive the OAuth token it described");
        (await TokenOfAsync(credentialId).ConfigureAwait(false)).ShouldBe("glpat-fresh");
        gitlab.IsAccessTokenLive("at-0").ShouldBeFalse("the OAuth pair the token replaced is ended at the provider");
        (await LoadIdentitiesAsync(world.MayaId).ConfigureAwait(false)).ShouldHaveSingleItem().CredentialId.ShouldBe(credentialId);
    }

    [Fact]
    public async Task A_replacement_token_the_provider_rejects_changes_nothing()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var credentialId = await SeedTokenCredentialAsync(world, world.MayaId, "glpat-expired", status: CredentialStatus.Error, lastError: "401 Unauthorized").ConfigureAwait(false);
        var before = await LoadCredentialAsync(credentialId).ConfigureAwait(false);

        var refusal = await Should.ThrowAsync<InvalidOperationException>(() => ReplaceTokenAsync(world, world.MayaId, credentialId, "glpat-typo")).ConfigureAwait(false);

        refusal.Message.ShouldContain("Could not validate the token against GitLab");
        var after = await LoadCredentialAsync(credentialId).ConfigureAwait(false);
        after.EncryptedPayload.ShouldBe(before.EncryptedPayload);
        after.Status.ShouldBe(CredentialStatus.Error);
        after.LastError.ShouldBe("401 Unauthorized");
    }

    [Fact]
    public async Task A_replacement_token_of_another_account_is_refused()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var credentialId = await SeedTokenCredentialAsync(world, world.MayaId, "glpat-expired", status: CredentialStatus.Error).ConfigureAwait(false);
        await SeedIdentityAsync(world, world.MayaId, credentialId, "maya").ConfigureAwait(false);
        gitlab.SeedPersonalAccessToken("nacho", "glpat-nachos");

        var refusal = await Should.ThrowAsync<CredentialAccountMismatchException>(() => ReplaceTokenAsync(world, world.MayaId, credentialId, "glpat-nachos")).ConfigureAwait(false);

        refusal.ExpectedAccount.ShouldBe("maya");
        refusal.PresentedAccount.ShouldBe("nacho");
        (await TokenOfAsync(credentialId).ConfigureAwait(false)).ShouldBe("glpat-expired");
    }

    [Fact]
    public async Task A_team_service_token_is_replaced_by_another_token_of_its_kind_whoever_it_belongs_to()
    {
        // A group access token's account is a bot that a new token for the same group does not share, so there is no person's
        // account to compare — and it stays a group access token, owned by the team.
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var credentialId = await SeedTokenCredentialAsync(world, null, "glpat-group-old", authType: AuthType.GroupAccessToken, ownership: CredentialOwnership.TeamService, status: CredentialStatus.Error).ConfigureAwait(false);
        var repositoryId = await SeedRepositoryAsync(world, credentialId, RepositoryStatus.Error).ConfigureAwait(false);
        gitlab.SeedPersonalAccessToken("group_42_bot", "glpat-group-new");

        await ReplaceTokenAsync(world, world.BossId, credentialId, "glpat-group-new").ConfigureAwait(false);

        var row = await LoadCredentialAsync(credentialId).ConfigureAwait(false);
        row.ShouldSatisfyAllConditions(r => r.AuthType.ShouldBe(AuthType.GroupAccessToken), r => r.Ownership.ShouldBe(CredentialOwnership.TeamService), r => r.OwnerUserId.ShouldBeNull(), r => r.Status.ShouldBe(CredentialStatus.Active));
        (await TokenOfAsync(credentialId).ConfigureAwait(false)).ShouldBe("glpat-group-new");
        (await LoadRepositoryAsync(repositoryId).ConfigureAwait(false)).Status.ShouldBe(RepositoryStatus.Active);
    }

    [Fact]
    public async Task Only_the_owner_of_a_personal_credential_or_someone_who_manages_credentials_may_replace_its_token()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var credentialId = await SeedTokenCredentialAsync(world, world.MayaId, "glpat-expired", status: CredentialStatus.Error).ConfigureAwait(false);
        var otherMember = await SeedMemberAsync(world, TeamRole.Member).ConfigureAwait(false);
        var outsider = await SeedUserAsync().ConfigureAwait(false);
        gitlab.SeedPersonalAccessToken("maya", "glpat-fresh");

        await Should.ThrowAsync<TenantAccessDeniedException>(() => ReplaceTokenAsync(world, otherMember, credentialId, "glpat-fresh")).ConfigureAwait(false);
        await Should.ThrowAsync<TenantAccessDeniedException>(() => ReplaceTokenAsync(world, world.ViewerId, credentialId, "glpat-fresh")).ConfigureAwait(false);
        await Should.ThrowAsync<TenantAccessDeniedException>(() => ReplaceTokenAsync(world, outsider, credentialId, "glpat-fresh")).ConfigureAwait(false);
        (await TokenOfAsync(credentialId).ConfigureAwait(false)).ShouldBe("glpat-expired", "every refusal left the credential as it was");

        await ReplaceTokenAsync(world, world.BossId, credentialId, "glpat-fresh").ConfigureAwait(false);

        (await TokenOfAsync(credentialId).ConfigureAwait(false)).ShouldBe("glpat-fresh");
    }

    [Fact]
    public async Task A_token_is_refused_for_a_credential_whose_person_no_longer_stands_in_the_team()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var credentialId = await SeedTokenCredentialAsync(world, world.MayaId, "glpat-expired", status: CredentialStatus.Error).ConfigureAwait(false);
        gitlab.SeedPersonalAccessToken("maya", "glpat-fresh");

        await RemoveMembershipAsync(world, world.MayaId).ConfigureAwait(false);

        var refusal = await Should.ThrowAsync<CredentialRetiredException>(() => ReplaceTokenAsync(world, world.BossId, credentialId, "glpat-fresh")).ConfigureAwait(false);

        refusal.Message.ShouldContain("standing");
        (await TokenOfAsync(credentialId).ConfigureAwait(false)).ShouldBe("glpat-expired", "the departed-token sweep retires it; a token written into it now would be retired with it");
    }

    [Fact]
    public async Task A_credential_of_another_team_is_not_reachable_even_by_an_instance_admin_working_in_this_one()
    {
        // The credential marker lets an instance Admin through on role alone, so the team they work in is checked by the service.
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var elsewhere = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var foreign = await SeedTokenCredentialAsync(elsewhere, elsewhere.MayaId, "glpat-foreign").ConfigureAwait(false);
        gitlab.SeedPersonalAccessToken("maya", "glpat-fresh");
        var instanceAdmin = await SeedUserAsync().ConfigureAwait(false);

        await Should.ThrowAsync<KeyNotFoundException>(() => ReplaceTokenAsync(world, instanceAdmin, foreign, "glpat-fresh", Roles.Admin)).ConfigureAwait(false);

        (await TokenOfAsync(foreign).ConfigureAwait(false)).ShouldBe("glpat-foreign");
    }

    [Fact]
    public async Task A_retired_credential_cannot_take_a_token_and_a_blank_token_is_refused_before_anything_is_asked()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var retired = await SeedTokenCredentialAsync(world, world.MayaId, "glpat-old", status: CredentialStatus.Revoked, retired: true).ConfigureAwait(false);
        var live = await SeedTokenCredentialAsync(world, world.MayaId, "glpat-live").ConfigureAwait(false);
        var instanceAdmin = await SeedUserAsync().ConfigureAwait(false);
        gitlab.SeedPersonalAccessToken("maya", "glpat-fresh");

        // The credential marker already reads a retired credential as not accessible to anyone on the team; an instance Admin
        // passes it by role and reaches the service, which says what it is.
        await Should.ThrowAsync<TenantAccessDeniedException>(() => ReplaceTokenAsync(world, world.BossId, retired, "glpat-fresh")).ConfigureAwait(false);
        var refusal = await Should.ThrowAsync<CredentialRetiredException>(() => ReplaceTokenAsync(world, instanceAdmin, retired, "glpat-fresh", Roles.Admin)).ConfigureAwait(false);
        await Should.ThrowAsync<ArgumentException>(() => ReplaceTokenAsync(world, world.MayaId, live, "   ")).ConfigureAwait(false);

        refusal.Code.ShouldBe(FailureCodes.CredentialRetired);
        (await LoadCredentialAsync(retired).ConfigureAwait(false)).EncryptedPayload.ShouldBeEmpty();
        (await TokenOfAsync(live).ConfigureAwait(false)).ShouldBe("glpat-live");
        gitlab.Count(StubEvent.Api200).ShouldBe(0, "a refused replacement asks the provider nothing");
    }

    // ─── Concurrency: the refresh lock ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_refresh_that_arrives_while_a_re_authorization_is_writing_waits_and_then_finds_the_new_pair_and_does_not_rotate_it()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        gitlab.Seed(GitLabDoorkeeperStub.DefaultClientId, "nacho", "at-0", "rt-0");
        var credentialId = await SeedOAuthCredentialAsync(world, world.NachoId, "at-0", "rt-0", expired: true).ConfigureAwait(false);
        var repositoryId = await SeedRepositoryAsync(world, credentialId, RepositoryStatus.Active).ConfigureAwait(false);
        await SeedIdentityAsync(world, world.NachoId, credentialId, "nacho").ConfigureAwait(false);
        var writer = new GatedReauthorizationWriter();
        var init = await InitAsync(world, world.NachoId, credentialId).ConfigureAwait(false);

        var reconnect = Task.Run(() => CompleteAsync(world, init, "authz:nacho", b => b.Register(c => writer.Over(new CredentialReauthorizationWriter(c.Resolve<CodeSpaceConnectionString>()))).As<ICredentialReauthorizationWriter>().InstancePerLifetimeScope()));
        await WithinPatienceAsync(writer.Entered, "the re-authorization reached its write", credentialId).ConfigureAwait(false);

        var refresh = Task.Run(() => CredentialRefreshHarness.ResolveTokenAsync(_fixture, repositoryId));
        await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        refresh.IsCompleted.ShouldBeFalse("the refresher takes the same lock the re-authorization holds, so it must wait for the write");
        gitlab.Count(StubEvent.Rotated).ShouldBe(0, "a refresh that rotated rt-0 now would have consumed the pair being replaced");

        writer.Release();
        await WithinPatienceAsync(reconnect, "the re-authorization finished once released", credentialId).ConfigureAwait(false);
        var token = await WithinPatienceAsync(refresh, "the refresh that waited for it finished", credentialId).ConfigureAwait(false);

        Dump(gitlab);
        var stored = await StoredPairAsync(credentialId).ConfigureAwait(false);
        token.ShouldBe(stored.AccessToken, "the refresher read the row after the lock and found the new pair, fresh");
        gitlab.Count(StubEvent.Rotated).ShouldBe(0);
        gitlab.Count(StubEvent.Refused).ShouldBe(0);
        gitlab.IsAccessTokenLive(stored.AccessToken).ShouldBeTrue();
    }

    [Fact]
    public async Task A_re_authorization_that_arrives_while_a_refresh_is_at_the_provider_waits_and_replaces_the_pair_the_refresh_stored()
    {
        using var gitlab = new GitLabDoorkeeperStub { RotateReplyDelay = TimeSpan.FromSeconds(3) };
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        gitlab.Seed(GitLabDoorkeeperStub.DefaultClientId, "nacho", "at-0", "rt-0");
        var credentialId = await SeedOAuthCredentialAsync(world, world.NachoId, "at-0", "rt-0", expired: true).ConfigureAwait(false);
        var repositoryId = await SeedRepositoryAsync(world, credentialId, RepositoryStatus.Active).ConfigureAwait(false);
        await SeedIdentityAsync(world, world.NachoId, credentialId, "nacho").ConfigureAwait(false);
        var init = await InitAsync(world, world.NachoId, credentialId).ConfigureAwait(false);

        var refresh = Task.Run(() => CredentialRefreshHarness.ResolveTokenAsync(_fixture, repositoryId));
        await WaitForAsync(() => gitlab.Count(StubEvent.Rotated) == 1, "GitLab rotated rt-0 for the refresher").ConfigureAwait(false);

        var reconnect = Task.Run(() => CompleteAsync(world, init, "authz:nacho"));
        await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        reconnect.IsCompleted.ShouldBeFalse("the re-authorization waits for the lock the refresher holds while its pair is at the provider");

        var rotatedToken = await WithinPatienceAsync(refresh, "the refresh that held the lock finished", credentialId).ConfigureAwait(false);
        await WithinPatienceAsync(reconnect, "the re-authorization that waited for it finished", credentialId).ConfigureAwait(false);

        Dump(gitlab);
        var stored = await StoredPairAsync(credentialId).ConfigureAwait(false);
        stored.AccessToken.ShouldNotBe(rotatedToken, "the re-authorization wrote over the pair the refresh had just stored — and read it first, so it replaced what was really there");
        gitlab.Count(StubEvent.Refused).ShouldBe(0, "nobody presented a consumed refresh token");
        gitlab.IsAccessTokenLive(rotatedToken).ShouldBeFalse("the pair the refresh stored is the one replaced, so it is the one ended at the provider");
        gitlab.IsAccessTokenLive(stored.AccessToken).ShouldBeTrue();
    }

    [Fact]
    public async Task A_re_authorization_waits_for_the_advisory_lock_another_pod_holds_as_well_as_for_this_processs_own()
    {
        // The in-process semaphore only excludes this pod. Another pod's refresher holds the Postgres advisory lock for the
        // credential while its pair is at the provider, and the key must be the one the refresher derives — the previous build
        // derives it the same way — or a re-authorization on this pod would write over a rotation on that one.
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var credentialId = await SeedOAuthCredentialAsync(world, world.NachoId, "at-0", "rt-0").ConfigureAwait(false);
        var init = await InitAsync(world, world.NachoId, credentialId).ConfigureAwait(false);
        IAsyncDisposable otherPod;

        using (var holder = _fixture.BeginScope())
            otherPod = await holder.Resolve<ICrossProcessLock>().AcquireAsync(CredentialRefreshLock.KeyOf(credentialId), CancellationToken.None).ConfigureAwait(false);

        var reconnect = Task.Run(() => CompleteAsync(world, init, "authz:nacho"));
        await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        try
        {
            reconnect.IsCompleted.ShouldBeFalse("another pod holds the credential's advisory lock, so this re-authorization must wait for it");
            (await StoredPairAsync(credentialId).ConfigureAwait(false)).AccessToken.ShouldBe("at-0", "nothing was written while the lock was held elsewhere");
        }
        finally
        {
            await otherPod.DisposeAsync().ConfigureAwait(false);
        }

        await WithinPatienceAsync(reconnect, "the re-authorization finished once the other pod let go", credentialId).ConfigureAwait(false);

        (await StoredPairAsync(credentialId).ConfigureAwait(false)).AccessToken.ShouldNotBe("at-0");
    }

    [Theory]
    [InlineData(PodBuild.This)]
    [InlineData(PodBuild.Previous)]
    public async Task A_re_authorization_waits_for_a_refresh_running_in_another_process_of_either_build_and_then_replaces_the_pair_it_stored(PodBuild build)
    {
        // A rolling deploy or a rollback runs pods of this build beside pods of the one before it. The previous build's refresher
        // (pinned in CodeSpace.StorageTestWorker) takes the advisory lock on the same key, so a re-authorization on this build
        // cannot write over a rotation it has under way — the one case an in-process test cannot make.
        using var gitlab = new GitLabDoorkeeperStub { RotateReplyDelay = TimeSpan.FromSeconds(4) };
        using var pods = new RefreshPodHarness(_fixture.ConnectionString);
        gitlab.Seed(GitLabDoorkeeperStub.DefaultClientId, "nacho", "at-0", "rt-0");
        gitlab.Seed(GitLabDoorkeeperStub.DefaultClientId, "nacho", "at-new", "rt-new");
        var credentialId = await pods.SeedCredentialAsync(gitlab.BaseUrl, "at-0", "rt-0").ConfigureAwait(false);
        var (teamId, instanceId) = await TeamAndInstanceOfAsync(credentialId).ConfigureAwait(false);

        var podRun = Task.Run(() => pods.RunAsync(credentialId, new PodSpec("pod", build, 1)));
        await WaitForAsync(() => gitlab.Count(StubEvent.Rotated) == 1, $"GitLab rotated rt-0 for the {build} pod, which now holds the credential's lock while its answer is delayed").ConfigureAwait(false);

        var reauthorization = Task.Run(() => ReauthorizeDirectlyAsync(credentialId, teamId, instanceId, new OAuthPayload { AccessToken = "at-new", RefreshToken = "rt-new", ExpiresAt = DateTimeOffset.UtcNow.AddHours(2) }));
        await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        reauthorization.IsCompleted.ShouldBeFalse($"the {build} pod holds the credential's advisory lock while its pair is at the provider");

        var callers = await WithinPatienceAsync(podRun, $"the {build} pod finished its refresh", credentialId).ConfigureAwait(false);
        await WithinPatienceAsync(reauthorization, "the re-authorization finished once the pod let go", credentialId).ConfigureAwait(false);

        Dump(gitlab);
        callers.ShouldAllBe(c => c.Ok, string.Join(" | ", callers.Select(c => c.ToString())));
        gitlab.Count(StubEvent.Refused).ShouldBe(0, $"nobody presented a consumed refresh token: {string.Join(", ", gitlab.RefusedRefreshTokens())}");
        (await StoredPairAsync(credentialId).ConfigureAwait(false)).AccessToken.ShouldBe("at-new", "the re-authorization wrote last, over the pair the pod's refresh had stored");
    }

    [Fact]
    public async Task Two_reconnects_racing_leave_the_credential_holding_one_live_pair_and_end_the_other()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        gitlab.Seed(GitLabDoorkeeperStub.DefaultClientId, "nacho", "at-0", "rt-0");
        var credentialId = await SeedOAuthCredentialAsync(world, world.NachoId, "at-0", "rt-0").ConfigureAwait(false);
        await SeedIdentityAsync(world, world.NachoId, credentialId, "nacho").ConfigureAwait(false);
        var first = await InitAsync(world, world.NachoId, credentialId).ConfigureAwait(false);
        var second = await InitAsync(world, world.NachoId, credentialId).ConfigureAwait(false);

        await WithinPatienceAsync(Task.WhenAll(Task.Run(() => CompleteAsync(world, first, "authz:nacho")), Task.Run(() => CompleteAsync(world, second, "authz:nacho"))), "both racing re-authorizations finished", credentialId).ConfigureAwait(false);

        Dump(gitlab);
        var stored = await StoredPairAsync(credentialId).ConfigureAwait(false);
        gitlab.IsAccessTokenLive(stored.AccessToken).ShouldBeTrue("what the row holds is live");
        gitlab.Rows.Count(r => r.User == "nacho" && r.RevokedAt == null).ShouldBe(1, "the seeded pair and the pair the loser wrote were each replaced, and so ended; one pair is live and it is the row's");
        gitlab.Count(StubEvent.Authorized).ShouldBe(2);
        (await CountCredentialsAsync(world).ConfigureAwait(false)).ShouldBe(1);
        (await LoadIdentitiesAsync(world.NachoId).ConfigureAwait(false)).Count(i => i.DeletedDate == null).ShouldBe(1, "one live act-as identity, as the unique index requires");
    }

    [Fact]
    public async Task A_retirement_that_lands_while_the_new_pair_is_being_written_is_not_undone()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var credentialId = await SeedOAuthCredentialAsync(world, world.NachoId, "at-0", "rt-0").ConfigureAwait(false);
        var repositoryId = await SeedRepositoryAsync(world, credentialId, RepositoryStatus.Error).ConfigureAwait(false);
        var writer = new GatedReauthorizationWriter();
        var init = await InitAsync(world, world.NachoId, credentialId).ConfigureAwait(false);

        var reconnect = Task.Run(() => CompleteAsync(world, init, "authz:nacho", b => b.Register(c => writer.Over(new CredentialReauthorizationWriter(c.Resolve<CodeSpaceConnectionString>()))).As<ICredentialReauthorizationWriter>().InstancePerLifetimeScope()));
        await WithinPatienceAsync(writer.Entered, "the re-authorization reached its write", credentialId).ConfigureAwait(false);

        await RetireAsync(credentialId).ConfigureAwait(false);
        writer.Release();

        await Should.ThrowAsync<CredentialRetiredException>(() => WithinPatienceAsync(reconnect, "the re-authorization finished once released", credentialId)).ConfigureAwait(false);

        var row = await LoadCredentialAsync(credentialId).ConfigureAwait(false);
        row.EncryptedPayload.ShouldBeEmpty("the compare-and-swap found the row retired and wrote nothing");
        row.DeletedDate.ShouldNotBeNull();
        (await LoadRepositoryAsync(repositoryId).ConfigureAwait(false)).Status.ShouldBe(RepositoryStatus.Error, "nothing was revived for a credential that stays retired");
    }

    [Fact]
    public async Task A_retirement_that_lands_on_a_row_a_disconnect_had_already_blanked_is_not_undone()
    {
        // The case the payload comparison cannot see: a Disconnect leaves the row blank but listed, so the re-authorization
        // reads '' and the write expects ''. A retirement that lands in between blanks nothing further — only the soft-delete
        // tells the write the row is gone, and that is the one thing standing between it and a live token in a retired row.
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var credentialId = await SeedOAuthCredentialAsync(world, world.NachoId, "at-0", "rt-0", disconnected: true).ConfigureAwait(false);
        var writer = new GatedReauthorizationWriter();
        var init = await InitAsync(world, world.NachoId, credentialId).ConfigureAwait(false);

        var reconnect = Task.Run(() => CompleteAsync(world, init, "authz:nacho", b => b.Register(c => writer.Over(new CredentialReauthorizationWriter(c.Resolve<CodeSpaceConnectionString>()))).As<ICredentialReauthorizationWriter>().InstancePerLifetimeScope()));
        await WithinPatienceAsync(writer.Entered, "the re-authorization reached its write", credentialId).ConfigureAwait(false);

        await RetireAsync(credentialId).ConfigureAwait(false);
        writer.Release();

        await Should.ThrowAsync<CredentialRetiredException>(() => WithinPatienceAsync(reconnect, "the re-authorization finished once released", credentialId)).ConfigureAwait(false);

        var row = await LoadCredentialAsync(credentialId).ConfigureAwait(false);
        row.EncryptedPayload.ShouldBeEmpty("the row was already blank, so only the soft-delete could refuse the write — and it did");
        row.DeletedDate.ShouldNotBeNull();
        row.Status.ShouldBe(CredentialStatus.Revoked);
    }

    [Fact]
    public async Task A_disconnect_that_blanks_the_row_while_the_new_pair_is_being_written_is_read_again_and_the_write_lands()
    {
        // A Disconnect does not take the refresh lock. It blanks the payload between the read and the write; the write is a
        // compare-and-swap on what was read, so it finds the row changed, reads it again — blank — and lands.
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var credentialId = await SeedOAuthCredentialAsync(world, world.NachoId, "at-0", "rt-0").ConfigureAwait(false);
        var interfering = new GatedReauthorizationWriter { BeforeFirstWrite = () => DisconnectBehindItsBackAsync(credentialId) };
        var init = await InitAsync(world, world.NachoId, credentialId).ConfigureAwait(false);

        var finished = await CompleteAsync(world, init, "authz:nacho", b => b.Register(c => interfering.Over(new CredentialReauthorizationWriter(c.Resolve<CodeSpaceConnectionString>()))).As<ICredentialReauthorizationWriter>().InstancePerLifetimeScope()).ConfigureAwait(false);

        finished.CredentialId.ShouldBe(credentialId);
        interfering.Writes.ShouldBe(2, "the first write lost to the Disconnect, the second landed");
        var row = await LoadCredentialAsync(credentialId).ConfigureAwait(false);
        row.Status.ShouldBe(CredentialStatus.Active);
        (await StoredPairAsync(credentialId).ConfigureAwait(false)).AccessToken.ShouldBe(gitlab.LiveAccessTokenOf("nacho"));
        gitlab.Count(StubEvent.Revoked).ShouldBe(0, "the row held nothing when it was replaced, so there was nothing to end");
    }

    // ─── Plumbing ──────────────────────────────────────────────────────────────────────────────────────────

    private sealed record World(Guid TeamId, Guid InstanceId, Guid NachoId, Guid MayaId, Guid BossId, Guid ViewerId, GitLabDoorkeeperStub GitLab);

    private async Task<World> SeedWorldAsync(GitLabDoorkeeperStub gitlab)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var team = new Team { Id = Guid.NewGuid(), Slug = $"t-{suffix}", Name = "Team" };
        var users = new[] { "nacho", "maya", "boss", "viewer" }.Select(label => new User { Id = Guid.NewGuid(), Email = $"{label}-{suffix}@x", Name = $"{label}-{suffix}" }).ToArray();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.Team.Add(team);
        db.User.AddRange(users);
        db.TeamMembership.AddRange(Membership(team.Id, users[0].Id, TeamRole.Admin), Membership(team.Id, users[1].Id, TeamRole.Member), Membership(team.Id, users[2].Id, TeamRole.Admin), Membership(team.Id, users[3].Id, TeamRole.Viewer));
        await db.SaveChangesAsync().ConfigureAwait(false);

        var world = new World(team.Id, Guid.Empty, users[0].Id, users[1].Id, users[2].Id, users[3].Id, gitlab);

        return world with { InstanceId = await SeedInstanceAsync(world, gitlab).ConfigureAwait(false) };
    }

    /// <summary>A GitLab instance whose OAuth app is the stub's — or, named, another app on the same host, which is what lets one team hold two instances of one provider.</summary>
    private async Task<Guid> SeedInstanceAsync(World world, GitLabDoorkeeperStub gitlab, string clientId = GitLabDoorkeeperStub.DefaultClientId) => await SeedInstanceAtAsync(world, gitlab.BaseUrl, clientId).ConfigureAwait(false);

    /// <summary>The same instance at any address — a provider that has stopped answering is one too.</summary>
    private async Task<Guid> SeedInstanceAtAsync(World world, string baseUrl, string clientId = GitLabDoorkeeperStub.DefaultClientId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = world.TeamId, Provider = ProviderKind.GitLab, DisplayName = $"gitlab-{Guid.NewGuid():N}"[..14], BaseUrl = baseUrl, ApiUrl = baseUrl, OauthClientId = clientId, OauthClientSecretEnc = scope.Resolve<IPayloadEncryptor>().Encrypt(GitLabDoorkeeperStub.DefaultClientSecret) };

        db.ProviderInstance.Add(instance);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return instance.Id;
    }

    private static TeamMembership Membership(Guid teamId, Guid userId, TeamRole role) => new() { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = role };

    /// <summary>
    /// An OAuth credential as the connect that made it leaves it. <paramref name="disconnected"/> is what a Disconnect leaves:
    /// Revoked, the payload blanked, the row still listed; <paramref name="retired"/> is what a departure or a connection
    /// delete leaves on top of that — soft-deleted.
    /// </summary>
    private async Task<Guid> SeedOAuthCredentialAsync(World world, Guid? ownerId, string accessToken, string refreshToken, bool disconnected = false, bool retired = false, bool expired = false, CredentialStatus status = CredentialStatus.Active, string? lastError = null, CredentialOwnership ownership = CredentialOwnership.Personal, Guid? instanceId = null, Guid? id = null)
    {
        using var scope = _fixture.BeginScope();
        var payload = new OAuthPayload { AccessToken = accessToken, RefreshToken = refreshToken, ExpiresAt = expired ? DateTimeOffset.UtcNow.AddMinutes(-1) : DateTimeOffset.UtcNow.AddHours(2) };
        var sealedPayload = scope.Resolve<IPayloadEncryptor>().Encrypt(scope.Resolve<ICredentialPayloadSerializer>().Serialize(payload));

        return await InsertCredentialAsync(scope, world, ownerId, AuthType.OAuth, sealedPayload, payload.ExpiresAt, disconnected || retired, retired, status, lastError, ownership, instanceId, id).ConfigureAwait(false);
    }

    private async Task<Guid> SeedTokenCredentialAsync(World world, Guid? ownerId, string token, AuthType authType = AuthType.Pat, CredentialOwnership ownership = CredentialOwnership.Personal, CredentialStatus status = CredentialStatus.Active, string? lastError = null, bool retired = false, DateTimeOffset? expires = null, Guid? instanceId = null, Guid? id = null)
    {
        using var scope = _fixture.BeginScope();
        CredentialPayload payload = authType == AuthType.GroupAccessToken ? new GroupAccessTokenPayload { Token = token } : new PatPayload { Token = token };
        var sealedPayload = scope.Resolve<IPayloadEncryptor>().Encrypt(scope.Resolve<ICredentialPayloadSerializer>().Serialize(payload));

        return await InsertCredentialAsync(scope, world, ownerId, authType, retired ? string.Empty : sealedPayload, expires, retired, retired, status, lastError, ownership, instanceId, id).ConfigureAwait(false);
    }

    private static async Task<Guid> InsertCredentialAsync(ILifetimeScope scope, World world, Guid? ownerId, AuthType authType, string encryptedPayload, DateTimeOffset? expires, bool blank, bool retired, CredentialStatus status, string? lastError, CredentialOwnership ownership, Guid? instanceId, Guid? id)
    {
        var db = scope.Resolve<CodeSpaceDbContext>();
        var actor = ownerId ?? world.BossId;

        var credential = new Credential
        {
            Id = id ?? Guid.NewGuid(),
            TeamId = world.TeamId,
            ProviderInstanceId = instanceId ?? world.InstanceId,
            OwnerUserId = ownerId,
            Ownership = ownership,
            AuthType = authType,
            DisplayName = ownerId == world.MayaId ? "GitLab · maya" : "GitLab · nacho",
            EncryptedPayload = blank ? string.Empty : encryptedPayload,
            ExpiresDate = expires,
            Status = blank ? CredentialStatus.Revoked : status,
            LastError = lastError,
            DeletedDate = retired ? DateTimeOffset.UtcNow.AddMinutes(-5) : null,
            CreatedBy = actor,
            LastModifiedBy = actor
        };

        db.Credential.Add(credential);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return credential.Id;
    }

    private async Task SeedIdentityAsync(World world, Guid userId, Guid credentialId, string account, bool deleted = false)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.UserProviderIdentity.Add(new UserProviderIdentity { Id = Guid.NewGuid(), UserId = userId, ProviderInstanceId = world.InstanceId, CredentialId = credentialId, ProviderUserId = world.GitLab.UserIdOf(account).ToString(), ProviderUsername = account, DeletedDate = deleted ? DateTimeOffset.UtcNow.AddMinutes(-5) : null });
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private async Task<Guid> SeedRepositoryAsync(World world, Guid credentialId, RepositoryStatus status)
    {
        var name = $"repo-{Guid.NewGuid():N}"[..13];
        var repository = new Repository { Id = Guid.NewGuid(), TeamId = world.TeamId, ProviderInstanceId = InstanceOf(world, credentialId), CredentialId = credentialId, ExternalId = $"acme/{name}", NamespacePath = "acme", Name = name, FullPath = $"acme/{name}", DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = $"{world.GitLab.BaseUrl}/acme/{name}", CloneUrlHttps = $"{world.GitLab.BaseUrl}/acme/{name}.git", Status = status, LastError = status == RepositoryStatus.Error ? CredentialRevocationService.DisconnectedRepositoryError : null };

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.Repository.Add(repository);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return repository.Id;
    }

    private Guid InstanceOf(World world, Guid credentialId)
    {
        using var scope = _fixture.BeginScope();

        return scope.Resolve<CodeSpaceDbContext>().Credential.AsNoTracking().Where(c => c.Id == credentialId).Select(c => c.ProviderInstanceId).Single();
    }

    private async Task<Guid> SeedHookAsync(World world, Guid credentialId, RepositoryWebhookRegistrationStatus status)
    {
        var id = Guid.NewGuid();
        var failed = status is RepositoryWebhookRegistrationStatus.Failed or RepositoryWebhookRegistrationStatus.DeadLettered;
        var registered = status == RepositoryWebhookRegistrationStatus.Registered;

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.ConnectionWebhook.Add(new ConnectionWebhook { Id = id, ProviderInstanceId = world.InstanceId, CredentialId = credentialId, OwnerPath = $"acme-{id:N}"[..13], CallbackUrl = $"https://codespace.test/api/webhooks/connection/{id}", SecretEnc = scope.Resolve<IPayloadEncryptor>().Encrypt("hook-secret"), SubscribedEvents = new List<string> { "push" }, RegistrationStatus = status, Attempts = 3, NextAttemptAt = DateTimeOffset.UtcNow.AddHours(1), LastError = failed ? "401 Unauthorized" : null, ExternalId = registered ? "4242" : null, RegisteredAt = registered ? DateTimeOffset.UtcNow.AddDays(-20) : null });
        await db.SaveChangesAsync().ConfigureAwait(false);

        return id;
    }

    private async Task<Guid> SeedMemberAsync(World world, TeamRole role)
    {
        var userId = await SeedUserAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.TeamMembership.Add(Membership(world.TeamId, userId, role));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return userId;
    }

    private async Task<Guid> SeedUserAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var user = new User { Id = Guid.NewGuid(), Email = $"u-{suffix}@x", Name = $"u-{suffix}" };

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.User.Add(user);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return user.Id;
    }

    private async Task RemoveMembershipAsync(World world, Guid userId)
    {
        using var scope = _fixture.BeginScope();

        await scope.Resolve<CodeSpaceDbContext>().TeamMembership.Where(m => m.TeamId == world.TeamId && m.UserId == userId).ExecuteDeleteAsync().ConfigureAwait(false);
    }

    /// <summary>A scope for one request as <paramref name="userId"/> in the world's team — anonymous when null, as the provider's callback is. Everything but GitLab is production's.</summary>
    private ILifetimeScope Scope(World world, Guid? userId, Action<ContainerBuilder>? configure = null, params string[] roles) => _fixture.BeginScope(b =>
    {
        if (userId is Guid id)
        {
            b.RegisterInstance(new TestCurrentUser(id, "tester", roles)).As<ICurrentUser>().SingleInstance();
            b.RegisterInstance(new TestCurrentTeam(world.TeamId)).As<ICurrentTeam>().SingleInstance();
        }

        configure?.Invoke(b);
    });

    /// <summary>The Providers modal's "Reconnect": Init naming the credential, then the provider's anonymous callback.</summary>
    private async Task<CompleteCredentialOAuthResult> ReconnectAsync(World world, Guid userId, Guid credentialId, string code, Action<ContainerBuilder>? configure = null)
    {
        var init = await InitAsync(world, userId, credentialId).ConfigureAwait(false);

        return await CompleteAsync(world, init, code, configure).ConfigureAwait(false);
    }

    private async Task<InitCredentialOAuthResult> InitAsync(World world, Guid userId, Guid? replaceCredentialId, Guid? intendedOwnerUserId = null)
    {
        using var scope = Scope(world, userId);

        return await scope.Resolve<IMediator>().Send(new InitCredentialOAuthCommand { ProviderInstanceId = world.InstanceId, DisplayName = "GitLab · nacho", ReplaceCredentialId = replaceCredentialId, IntendedOwnerUserId = intendedOwnerUserId }).ConfigureAwait(false);
    }

    private async Task<CompleteCredentialOAuthResult> CompleteAsync(World world, InitCredentialOAuthResult init, string code, Action<ContainerBuilder>? configure = null)
    {
        using var callback = Scope(world, userId: null, configure);

        return await callback.Resolve<IMediator>().Send(new CompleteCredentialOAuthCommand { State = init.State, Code = code, BrowserNonce = init.BrowserNonce }).ConfigureAwait(false);
    }

    private async Task<ReplaceCredentialTokenResult> ReplaceTokenAsync(World world, Guid userId, Guid credentialId, string token, params string[] roles)
    {
        using var scope = Scope(world, userId, configure: null, roles);

        return await scope.Resolve<IMediator>().Send(new ReplaceCredentialTokenCommand { CredentialId = credentialId, Token = token }).ConfigureAwait(false);
    }

    private async Task<(Guid TeamId, Guid InstanceId)> TeamAndInstanceOfAsync(Guid credentialId)
    {
        using var scope = _fixture.BeginScope();
        var credential = await scope.Resolve<CodeSpaceDbContext>().Credential.AsNoTracking().SingleAsync(c => c.Id == credentialId).ConfigureAwait(false);

        return (credential.TeamId, credential.ProviderInstanceId);
    }

    /// <summary>The re-authorization itself, with no connect around it — for a credential a process harness seeded, which has no team members or pending state.</summary>
    private async Task ReauthorizeDirectlyAsync(Guid credentialId, Guid teamId, Guid instanceId, OAuthPayload payload)
    {
        using var scope = _fixture.BeginScope();

        await scope.Resolve<ICredentialReauthorizer>().ReauthorizeAsync(new CredentialReauthorization { CredentialId = credentialId, TeamId = teamId, ProviderInstanceId = instanceId, Payload = payload, ActorUserId = Guid.NewGuid() }, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<Credential> LoadCredentialAsync(Guid credentialId) => await GitLabCredentialSeed.ReadCredentialAsync(_fixture, credentialId).ConfigureAwait(false);

    private async Task<OAuthPayload> StoredPairAsync(Guid credentialId) => await GitLabCredentialSeed.ReadStoredPayloadAsync(_fixture, credentialId).ConfigureAwait(false);

    /// <summary>The static token the row holds, read as stored — past the resolver's refusal of an Error credential, since a refused replacement leaves the credential as it was.</summary>
    private async Task<string> TokenOfAsync(Guid credentialId) => await GitLabCredentialSeed.ReadPayloadAsync(_fixture, credentialId).ConfigureAwait(false) switch
    {
        PatPayload pat => pat.Token,
        GroupAccessTokenPayload group => group.Token,
        var other => throw new InvalidOperationException($"the credential holds a {other.Type} payload, not a static token")
    };

    private async Task<Repository> LoadRepositoryAsync(Guid repositoryId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().Repository.AsNoTracking().SingleAsync(r => r.Id == repositoryId).ConfigureAwait(false);
    }

    private async Task<ConnectionWebhook> LoadHookAsync(Guid hookId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().ConnectionWebhook.AsNoTracking().SingleAsync(w => w.Id == hookId).ConfigureAwait(false);
    }

    private async Task<List<UserProviderIdentity>> LoadIdentitiesAsync(Guid userId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().UserProviderIdentity.AsNoTracking().Where(i => i.UserId == userId).OrderBy(i => i.CreatedDate).ToListAsync().ConfigureAwait(false);
    }

    private async Task<OAuthPendingState> LoadPendingStateAsync(string state)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().OAuthPendingState.AsNoTracking().SingleAsync(s => s.State == state).ConfigureAwait(false);
    }

    private async Task<int> CountCredentialsAsync(World world)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().Credential.AsNoTracking().CountAsync(c => c.TeamId == world.TeamId).ConfigureAwait(false);
    }

    private async Task<int> CountPendingStatesAsync(World world)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().OAuthPendingState.AsNoTracking().CountAsync(s => s.TeamId == world.TeamId).ConfigureAwait(false);
    }

    private bool WasDispatched(Guid hookId)
    {
        using var scope = _fixture.BeginScope();

        return scope.Resolve<InMemoryBackgroundJobClient>().Calls.Any(c => c.ServiceType == typeof(IConnectionWebhookRegistrar) && Equals(c.FirstArgument, hookId));
    }

    /// <summary>What a departure or a connection delete leaves: the token gone, the row soft-deleted and Revoked.</summary>
    private async Task RetireAsync(Guid credentialId)
    {
        using var scope = _fixture.BeginScope();

        await scope.Resolve<CodeSpaceDbContext>().Credential.Where(c => c.Id == credentialId).ExecuteUpdateAsync(s => s.SetProperty(c => c.EncryptedPayload, string.Empty).SetProperty(c => c.Status, CredentialStatus.Revoked).SetProperty(c => c.DeletedDate, DateTimeOffset.UtcNow)).ConfigureAwait(false);
    }

    /// <summary>A Disconnect, which takes no refresh lock: it blanks the payload and marks the row Revoked, and that is all that matters to a write waiting on the old payload.</summary>
    private async Task DisconnectBehindItsBackAsync(Guid credentialId)
    {
        using var scope = _fixture.BeginScope();

        await scope.Resolve<CodeSpaceDbContext>().Credential.Where(c => c.Id == credentialId).ExecuteUpdateAsync(s => s.SetProperty(c => c.EncryptedPayload, string.Empty).SetProperty(c => c.Status, CredentialStatus.Revoked)).ConfigureAwait(false);
    }

    /// <summary>The row a pod from before migration 0250 writes: only the columns it knows.</summary>
    private async Task InsertPreMigrationStateAsync(World world, string state, Guid initiatorId, string nonceHash)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        await using var cmd = new NpgsqlCommand("INSERT INTO oauth_pending_state (state, provider_instance_id, team_id, initiator_user_id, code_verifier, intended_display_name, intended_owner_user_id, return_url, browser_nonce_hash, requested_scopes, expires_date, created_date, created_by, last_modified_date, last_modified_by) VALUES (@state, @instance, @team, @user, 'verifier', 'GitLab · nacho', @user, NULL, @hash, NULL, now() + interval '10 minutes', now(), @user, now(), @user)", conn);
        cmd.Parameters.AddWithValue("state", state);
        cmd.Parameters.AddWithValue("instance", world.InstanceId);
        cmd.Parameters.AddWithValue("team", world.TeamId);
        cmd.Parameters.AddWithValue("user", initiatorId);
        cmd.Parameters.AddWithValue("hash", nonceHash);

        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Bounds a wait that hangs, if it hangs, on the refresh semaphore or the Postgres advisory lock — and says how to see which.
    /// </summary>
    private static async Task<T> WithinPatienceAsync<T>(Task<T> work, string signal, Guid credentialId)
    {
        await WithinPatienceAsync((Task)work, signal, credentialId).ConfigureAwait(false);

        return await work.ConfigureAwait(false);
    }

    private static async Task WithinPatienceAsync(Task work, string signal, Guid credentialId)
    {
        try
        {
            await work.WaitAsync(Patience).ConfigureAwait(false);
        }
        catch (TimeoutException timeout)
        {
            throw new TimeoutException($"{signal} within {Patience.TotalSeconds}s — most likely stuck on the in-process refresh semaphore or the Postgres advisory lock (key {CredentialRefreshLock.KeyOf(credentialId)}) for credential {credentialId}. While the test hangs, inspect `SELECT pid, pg_blocking_pids(pid), wait_event_type, state, query FROM pg_stat_activity WHERE datname = current_database()`.", timeout);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, string signal)
    {
        var deadline = DateTimeOffset.UtcNow + Patience;

        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException($"{signal} within {Patience.TotalSeconds}s");

            await Task.Delay(20).ConfigureAwait(false);
        }
    }

    private void Dump(GitLabDoorkeeperStub gitlab)
    {
        _output.WriteLine($"GitLab saw: rotated={gitlab.Count(StubEvent.Rotated)} refused={gitlab.Count(StubEvent.Refused)} authorized={gitlab.Count(StubEvent.Authorized)} revoked={gitlab.Count(StubEvent.Revoked)} api-200={gitlab.Count(StubEvent.Api200)} api-401={gitlab.Count(StubEvent.Api401)}");

        foreach (var entry in gitlab.Log) _output.WriteLine($"  {entry.At:HH:mm:ss.fff} {entry.Kind}: {entry.Detail}");
    }

    /// <summary>Records every revoke it is asked for, with what the credential row held at that moment — so "after the new pair is stored" is a fact the test reads, not an inference.</summary>
    private sealed class RecordingGrantRevoker : IOAuthGrantRevoker
    {
        private readonly Func<string?> _storedAccessToken;
        private readonly List<Revocation> _revocations = new();

        public RecordingGrantRevoker(Func<string?>? storedAccessToken = null) { _storedAccessToken = storedAccessToken ?? (() => null); }

        public bool Fails { get; init; }

        public IReadOnlyList<Revocation> Revocations { get { lock (_revocations) { return _revocations.ToList(); } } }

        public OAuthPayload? ReadPayload(Credential credential) => null;

        public Task RevokeAsync(ProviderInstance instance, string clientSecret, OAuthPayload payload, bool wholeGrant, CancellationToken cancellationToken)
        {
            lock (_revocations) { _revocations.Add(new Revocation(payload, wholeGrant, _storedAccessToken())); }

            return Fails ? throw new InvalidOperationException("simulated: the provider is down") : Task.CompletedTask;
        }

        public sealed record Revocation(OAuthPayload Payload, bool WholeGrant, string? StoredAccessTokenWhenAsked);
    }

    /// <summary>
    /// Wraps the real writer so a test can hold a re-authorization inside the lock — after the row was read and before it is
    /// written — or let something else change the row in that gap. <see cref="Entered"/> completes the moment the first write
    /// is reached; the write waits for <see cref="Release"/>.
    /// </summary>
    private sealed class GatedReauthorizationWriter : ICredentialReauthorizationWriter
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ICredentialReauthorizationWriter _inner = default!;
        private int _writes;

        public Task Entered => _entered.Task;

        /// <summary>Runs once, before the first write is attempted — what happens to the row while the re-authorization is between its read and its write.</summary>
        public Func<Task>? BeforeFirstWrite { get; init; }

        public int Writes => Volatile.Read(ref _writes);

        public void Release() => _release.TrySetResult();

        public ICredentialReauthorizationWriter Over(ICredentialReauthorizationWriter inner)
        {
            _inner = inner;

            return this;
        }

        public async Task<bool> TryReplaceAsync(CredentialReplacement replacement, CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _writes);

            if (attempt == 1 && BeforeFirstWrite != null)
            {
                await BeforeFirstWrite().ConfigureAwait(false);
            }
            else if (attempt == 1)
            {
                _entered.TrySetResult();
                await _release.Task.ConfigureAwait(false);
            }

            return await _inner.TryReplaceAsync(replacement, cancellationToken).ConfigureAwait(false);
        }
    }
}
