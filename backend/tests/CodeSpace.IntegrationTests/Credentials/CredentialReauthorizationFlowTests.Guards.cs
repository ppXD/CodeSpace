using System.Data.Common;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Shouldly;

namespace CodeSpace.IntegrationTests.Credentials;

/// <summary>
/// The guards a re-authorization keeps for itself. Every door in front of it — the Init lookup, the owner's standing at the
/// callback, the credential marker on PUT /token — refuses first, so a test that goes through a door passes whether or not the
/// service behind it checks anything; <see cref="ICredentialReauthorizer"/> is documented as reusable across entry points, and
/// the tests here call it directly (or break the commit under it) so that each guard is the thing that refuses. Plus the
/// account on record for a credential the act-as identity no longer names, which no door can see at all.
/// </summary>
public partial class CredentialReauthorizationFlowTests
{
    /// <summary>The two ways a person re-authorizes a credential: a token pasted over it, or OAuth signed in again.</summary>
    public enum Door
    {
        PastedToken,
        Reconnect
    }

    // ─── The account on record when the act-as identity names a newer credential ─────────────────────────────

    [Theory]
    [InlineData(Door.PastedToken)]
    [InlineData(Door.Reconnect)]
    public async Task A_credential_no_identity_names_any_more_still_refuses_another_account(Door door)
    {
        // An identity is one per (person, provider instance) and every connect re-points it at the newest credential, so an
        // older credential of the same person — the "token next to OAuth" state the Providers modal itself produces — is named
        // by nothing. Its account is still on record: it is the account the person's identity speaks for on that instance.
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var (older, newest) = await SeedOlderTokenThenConnectOAuthAsync(world).ConfigureAwait(false);

        var refusal = await Should.ThrowAsync<CredentialAccountMismatchException>(() => ReauthorizeAsNachoAsync(world, door, older, "maya")).ConfigureAwait(false);

        refusal.ShouldSatisfyAllConditions(r => r.ExpectedAccount.ShouldBe("nacho"), r => r.PresentedAccount.ShouldBe("maya"));
        (await TokenOfAsync(older).ConfigureAwait(false)).ShouldBe("glpat-nacho-old", "the row was not touched");
        (await LoadCredentialAsync(older).ConfigureAwait(false)).Status.ShouldBe(CredentialStatus.Active);

        var identity = (await LoadIdentitiesAsync(world.NachoId).ConfigureAwait(false)).Single(i => i.DeletedDate == null);
        identity.ShouldSatisfyAllConditions(i => i.CredentialId.ShouldBe(newest), i => i.ProviderUsername.ShouldBe("nacho"), i => i.ProviderUserId.ShouldBe(gitlab.UserIdOf("nacho").ToString()));
    }

    [Theory]
    [InlineData(Door.PastedToken)]
    [InlineData(Door.Reconnect)]
    public async Task The_same_account_re_authorizes_a_credential_no_identity_names_any_more_and_the_identity_still_speaks_for_that_account(Door door)
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var (older, _) = await SeedOlderTokenThenConnectOAuthAsync(world).ConfigureAwait(false);
        var before = await LoadCredentialAsync(older).ConfigureAwait(false);

        await ReauthorizeAsNachoAsync(world, door, older, "nacho").ConfigureAwait(false);

        var after = await LoadCredentialAsync(older).ConfigureAwait(false);
        after.EncryptedPayload.ShouldNotBe(before.EncryptedPayload, "the new authorization is in the same row");
        after.LastModifiedBy.ShouldBe(world.NachoId);
        (await CountCredentialsAsync(world).ConfigureAwait(false)).ShouldBe(2, "no row was minted");

        var identity = (await LoadIdentitiesAsync(world.NachoId).ConfigureAwait(false)).Single(i => i.DeletedDate == null);
        identity.ShouldSatisfyAllConditions(i => i.ProviderUsername.ShouldBe("nacho"), i => i.ProviderUserId.ShouldBe(gitlab.UserIdOf("nacho").ToString()));
    }

    // ─── The guards the reauthorizer keeps when nothing stands in front of it ────────────────────────────────

    [Fact]
    public async Task The_reauthorizer_itself_reads_a_credential_of_another_team_as_not_found()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var elsewhere = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var credentialId = await SeedOAuthCredentialAsync(world, world.NachoId, "at-0", "rt-0").ConfigureAwait(false);

        await Should.ThrowAsync<KeyNotFoundException>(() => ReauthorizeInWorldAsync(world, credentialId, FreshPair("at-new"), teamId: elsewhere.TeamId)).ConfigureAwait(false);

        (await StoredPairAsync(credentialId).ConfigureAwait(false)).AccessToken.ShouldBe("at-0", "another team's request wrote nothing into this team's credential");
        gitlab.Count(StubEvent.Api200).ShouldBe(0, "and asked the provider nothing");
    }

    [Fact]
    public async Task The_reauthorizer_itself_refuses_a_credential_whose_person_no_longer_stands_in_the_team()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var credentialId = await SeedOAuthCredentialAsync(world, world.MayaId, "at-0", "rt-0").ConfigureAwait(false);

        await RemoveMembershipAsync(world, world.MayaId).ConfigureAwait(false);

        var refusal = await Should.ThrowAsync<CredentialRetiredException>(() => ReauthorizeInWorldAsync(world, credentialId, FreshPair("at-new"), actorUserId: world.BossId)).ConfigureAwait(false);

        refusal.Message.ShouldContain("standing");
        (await StoredPairAsync(credentialId).ConfigureAwait(false)).AccessToken.ShouldBe("at-0", "the departed-token sweep retires it; a pair written into it now would be retired with it");
    }

    [Theory]
    [InlineData(true, "Could not confirm which GitLab account", "at-0")]  // an account is on record → comparing takes an answer, and none is a refusal
    [InlineData(false, null, "at-unknown")]                               // nothing to compare → a pair the provider just issued is presumed good
    public async Task A_pair_the_provider_cannot_answer_for_is_refused_only_when_an_account_has_to_be_compared(bool accountOnRecord, string? expectedRefusal, string expectedStoredAccessToken)
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var credentialId = await SeedOAuthCredentialAsync(world, world.NachoId, "at-0", "rt-0").ConfigureAwait(false);

        if (accountOnRecord) await SeedIdentityAsync(world, world.NachoId, credentialId, "nacho").ConfigureAwait(false);

        // A token GitLab never issued: its probe is a 401, so the provider says nothing about whose it is.
        var thrown = await Record.ExceptionAsync(() => ReauthorizeInWorldAsync(world, credentialId, FreshPair("at-unknown"))).ConfigureAwait(false);

        if (expectedRefusal == null) thrown.ShouldBeNull();
        else thrown.ShouldBeOfType<InvalidOperationException>().Message.ShouldContain(expectedRefusal);

        (await StoredPairAsync(credentialId).ConfigureAwait(false)).AccessToken.ShouldBe(expectedStoredAccessToken);
    }

    [Fact]
    public async Task A_unit_of_work_that_fails_after_the_new_pair_was_stored_revokes_nothing_at_the_provider()
    {
        // The new pair is the only copy of an authorization the provider will not issue again, so its write is durable on a
        // connection of its own and survives the rollback. The replaced pair is ended only once the unit of work commits: a
        // failed commit means the person repeats the re-authorization, and nothing was revoked on the strength of one that did
        // not finish. The commit is failed for real — the transaction interceptor throws as the pipeline commits.
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        gitlab.Seed(GitLabDoorkeeperStub.DefaultClientId, "nacho", "at-0", "rt-0");
        var credentialId = await SeedOAuthCredentialAsync(world, world.NachoId, "at-0", "rt-0").ConfigureAwait(false);
        var init = await InitAsync(world, world.NachoId, credentialId).ConfigureAwait(false);
        var options = OptionsWhoseCommitFails();

        await Should.ThrowAsync<InvalidOperationException>(() => CompleteAsync(world, init, "authz:nacho", b => b.RegisterInstance(options).As<DbContextOptions<CodeSpaceDbContext>>().SingleInstance())).ConfigureAwait(false);

        Dump(gitlab);
        var stored = await StoredPairAsync(credentialId).ConfigureAwait(false);
        stored.AccessToken.ShouldBe(gitlab.LiveAccessTokenOf("nacho"), "the new pair was stored before the commit and stays");
        gitlab.Count(StubEvent.Authorized).ShouldBe(1);
        gitlab.Count(StubEvent.Revoked).ShouldBe(0, "nothing was revoked: the unit of work that would have justified it never committed");
        gitlab.IsAccessTokenLive("at-0").ShouldBeTrue("the replaced pair is left to expire at the provider, as the service documents");
    }

    // ─── Plumbing ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A person with a token linked first and OAuth connected after, as production leaves them: the token's credential and the
    /// identity that names it (what LinkByPat writes), then a plain OAuth connect — the real flow — which re-points that one
    /// identity at the new credential. Checked, because a seed that left the identity on the older credential would be a
    /// state the product cannot produce and the tests above would pass for the wrong reason.
    /// </summary>
    private async Task<(Guid Older, Guid Newest)> SeedOlderTokenThenConnectOAuthAsync(World world)
    {
        var older = await SeedTokenCredentialAsync(world, world.NachoId, "glpat-nacho-old").ConfigureAwait(false);
        await SeedIdentityAsync(world, world.NachoId, older, "nacho").ConfigureAwait(false);

        var init = await InitAsync(world, world.NachoId, replaceCredentialId: null).ConfigureAwait(false);
        var newest = (await CompleteAsync(world, init, "authz:nacho").ConfigureAwait(false)).CredentialId!.Value;

        var live = (await LoadIdentitiesAsync(world.NachoId).ConfigureAwait(false)).Single(i => i.DeletedDate == null);
        live.CredentialId.ShouldBe(newest, "the plain connect re-pointed the one live identity at the newest credential, so nothing names the older one");

        return (older, newest);
    }

    /// <summary>Nacho re-authorizes <paramref name="credentialId"/> through <paramref name="door"/> with an authorization of <paramref name="account"/>.</summary>
    private async Task ReauthorizeAsNachoAsync(World world, Door door, Guid credentialId, string account)
    {
        if (door == Door.Reconnect)
        {
            await ReconnectAsync(world, world.NachoId, credentialId, $"authz:{account}").ConfigureAwait(false);

            return;
        }

        var token = $"glpat-{account}-{Guid.NewGuid():N}";
        world.GitLab.SeedPersonalAccessToken(account, token);

        await ReplaceTokenAsync(world, world.NachoId, credentialId, token).ConfigureAwait(false);
    }

    private static OAuthPayload FreshPair(string accessToken) => new() { AccessToken = accessToken, RefreshToken = $"rt-{accessToken}", ExpiresAt = DateTimeOffset.UtcNow.AddHours(2) };

    /// <summary>The service with nothing in front of it: the request as any entry point would build it, the team and actor chosen by the test.</summary>
    private async Task ReauthorizeInWorldAsync(World world, Guid credentialId, OAuthPayload payload, Guid? teamId = null, Guid? actorUserId = null)
    {
        using var scope = _fixture.BeginScope();

        await scope.Resolve<ICredentialReauthorizer>().ReauthorizeAsync(new CredentialReauthorization { CredentialId = credentialId, TeamId = teamId ?? world.TeamId, ProviderInstanceId = world.InstanceId, Payload = payload, ActorUserId = actorUserId ?? world.NachoId }, CancellationToken.None).ConfigureAwait(false);
    }

    private DbContextOptions<CodeSpaceDbContext> OptionsWhoseCommitFails()
    {
        DbContextOptions<CodeSpaceDbContext> production;

        using (var probe = _fixture.BeginScope())
            production = probe.Resolve<DbContextOptions<CodeSpaceDbContext>>();

        return new DbContextOptionsBuilder<CodeSpaceDbContext>(production).AddInterceptors(new FailingCommit()).Options;
    }

    /// <summary>Fails the commit of the first transaction a scope's context opens — the unit of work the mediator pipeline wraps around a command.</summary>
    private sealed class FailingCommit : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("simulated: the unit of work could not commit");
    }
}
