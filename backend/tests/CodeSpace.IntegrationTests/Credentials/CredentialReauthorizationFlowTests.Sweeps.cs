using System.Data.Common;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Shouldly;

namespace CodeSpace.IntegrationTests.Credentials;

/// <summary>
/// Where an in-place re-authorization meets the three things that move or end what hangs on a dead credential: the recurring
/// repair of stranded bindings, the connect-time carry-forward, and the recurring retirement of a departed member's tokens.
/// Each decides a credential is dead, and then acts on that verdict a moment later — so a re-authorization that lands in
/// between used to be acted against: its bindings moved away from a credential that was healthy again, or its new token
/// blanked. The claim is that the verdict is taken again under the lock every rewrite of a credential's token takes, so the
/// two can no longer interleave: whichever comes second finds what the first did.
///
/// <para>🟢 High fidelity — the real sweeps through the real mediator pipeline, the real re-authorization (a Reconnect, a pasted
/// token) with its refresh lock, and real Postgres. The gap is made, not hoped for: a command interceptor on the sweep's (or
/// the connect's) own database context runs the person's re-authorization right after the statement that read the candidate,
/// which is the one moment the verdict is stale.</para>
/// </summary>
public partial class CredentialReauthorizationFlowTests
{
    public enum DeadAs { Refused, Disconnected }

    public enum HeldBy { ThisProcess, AnotherPod }

    private static readonly TimeSpan LongEnoughToHaveFinished = TimeSpan.FromSeconds(1.5);

    /// <summary>Passes that would set a candidate aside had each of them counted as its failure: a credential somebody is writing is skipped, which is not one.</summary>
    private const int PassesThatWouldSetAFailingCandidateAside = SweepFailureBackoff.FailuresBeforeBackoff + 1;

    // ─── The repair sweep ──────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(DeadAs.Refused)]
    [InlineData(DeadAs.Disconnected)]
    public async Task A_re_authorization_that_lands_between_the_sweeps_read_and_its_move_leaves_the_bindings_on_the_credential(DeadAs deadAs)
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var stranded = await SeedStrandedAsync(world, deadAs).ConfigureAwait(false);
        var landed = new AfterReadOf(ReadsStrandedCandidates, () => ReconnectAsync(world, world.NachoId, stranded.Dead, "authz:nacho"));

        await SweepThroughAsync(landed).ConfigureAwait(false);

        landed.Ran.ShouldBeTrue("fixture check: the re-authorization landed after the sweep had read its candidates");
        (await LoadCredentialAsync(stranded.Dead).ConfigureAwait(false)).Status.ShouldBe(CredentialStatus.Active, "fixture check: the credential is healthy again");
        (await LoadRepositoryAsync(stranded.Repository).ConfigureAwait(false)).ShouldSatisfyAllConditions(r => r.CredentialId.ShouldBe(stranded.Dead, "the sweep judged a credential that was no longer dead, and so moved nothing"), r => r.Status.ShouldBe(RepositoryStatus.Active));
        (await LoadHookAsync(stranded.Hook).ConfigureAwait(false)).CredentialId.ShouldBe(stranded.Dead, "the group hook stays with its credential, revived by the re-authorization");
        await AssertNothingBoundAsync(stranded.Successor).ConfigureAwait(false);
    }

    [Fact]
    public async Task A_re_authorization_that_arrives_while_the_sweep_is_moving_waits_for_it_and_lands_after_the_move()
    {
        // The credential was dead when the sweep took its lock and judged it, so the move is right; what the re-authorization must
        // not do is write the credential's token while the move is being made and be left looking healthy and unbound by accident.
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var stranded = await SeedStrandedAsync(world, DeadAs.Disconnected).ConfigureAwait(false);
        var init = await InitAsync(world, world.NachoId, stranded.Dead).ConfigureAwait(false);
        Task<CompleteCredentialOAuthResult>? reconnect = null;
        var finishedWhileMoving = false;
        var arrives = new AfterReadOf(ReadsStrandedCandidates, async () =>
        {
            reconnect = Task.Run(() => CompleteAsync(world, init, "authz:nacho"));
            await Task.Delay(LongEnoughToHaveFinished).ConfigureAwait(false);
            finishedWhileMoving = reconnect.IsCompleted;
        }, nth: 2);

        await SweepThroughAsync(arrives).ConfigureAwait(false);

        arrives.Ran.ShouldBeTrue("fixture check: the sweep judged the credential again under its lock, and the re-authorization arrived right after");
        finishedWhileMoving.ShouldBeFalse("the re-authorization takes the lock the sweep holds while it moves, so it cannot finish inside the move");
        await WithinPatienceAsync(reconnect!, "the re-authorization that waited for the move finished", stranded.Dead).ConfigureAwait(false);

        (await LoadRepositoryAsync(stranded.Repository).ConfigureAwait(false)).ShouldSatisfyAllConditions(r => r.CredentialId.ShouldBe(stranded.Successor, "judged dead under the lock, so it moved"), r => r.Status.ShouldBe(RepositoryStatus.Active));
        (await LoadHookAsync(stranded.Hook).ConfigureAwait(false)).CredentialId.ShouldBe(stranded.Successor);
        (await LoadCredentialAsync(stranded.Dead).ConfigureAwait(false)).Status.ShouldBe(CredentialStatus.Active, "the re-authorization then landed on the credential, which has nothing bound to it any more");
    }

    [Fact]
    public async Task A_provider_accepting_a_refused_credential_again_before_the_sweeps_move_leaves_the_bindings_on_it()
    {
        // The health probe rechecks a refused credential every hour, and writes it back to Active without the refresh lock: it is
        // not a write of the token, so the lock cannot exclude it, and the verdict taken again once the credential is held has to.
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var stranded = await SeedStrandedAsync(world, DeadAs.Refused).ConfigureAwait(false);
        var accepted = false;
        var landed = new AfterReadOf(ReadsStrandedCandidates, async () => accepted = await AcceptAgainAsync(stranded.Dead).ConfigureAwait(false));

        await SweepThroughAsync(landed).ConfigureAwait(false);

        landed.Ran.ShouldBeTrue("fixture check: the provider accepted the token again after the sweep had read its candidates");
        accepted.ShouldBeTrue("fixture check: the verdict was recorded");
        (await LoadCredentialAsync(stranded.Dead).ConfigureAwait(false)).Status.ShouldBe(CredentialStatus.Active, "fixture check: the credential is healthy again");
        (await LoadRepositoryAsync(stranded.Repository).ConfigureAwait(false)).ShouldSatisfyAllConditions(r => r.CredentialId.ShouldBe(stranded.Dead, "the sweep judged a credential its provider had accepted again, and so moved nothing"), r => r.Status.ShouldBe(RepositoryStatus.Active));
        (await LoadHookAsync(stranded.Hook).ConfigureAwait(false)).CredentialId.ShouldBe(stranded.Dead);
        await AssertNothingBoundAsync(stranded.Successor).ConfigureAwait(false);
    }

    [Fact]
    public async Task A_provider_accepting_a_refused_credential_while_the_sweep_is_moving_waits_for_the_move_and_lands_after_it()
    {
        // The credential was dead when the sweep judged it under its lock, so the move is right. What the acceptance must not do is
        // land inside the move, between the verdict and the save, and leave a healthy credential whose bindings have gone.
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var stranded = await SeedStrandedAsync(world, DeadAs.Refused).ConfigureAwait(false);
        Task<bool>? accepted = null;
        var finishedWhileMoving = false;
        var arrives = new AfterReadOf(ReadsStrandedCandidates, async () =>
        {
            accepted = Task.Run(() => AcceptAgainAsync(stranded.Dead));
            await Task.Delay(LongEnoughToHaveFinished).ConfigureAwait(false);
            finishedWhileMoving = accepted.IsCompleted;
        }, nth: 2);

        await SweepThroughAsync(arrives).ConfigureAwait(false);

        arrives.Ran.ShouldBeTrue("fixture check: the sweep judged the credential again under its lock, and the acceptance arrived right after");
        finishedWhileMoving.ShouldBeFalse("the sweep holds the credential's row while it moves, so a write of that row has to wait for the commit");
        (await WithinPatienceAsync(accepted!, "the acceptance that waited for the move finished", stranded.Dead).ConfigureAwait(false)).ShouldBeTrue("it landed once the move had committed");

        (await LoadRepositoryAsync(stranded.Repository).ConfigureAwait(false)).ShouldSatisfyAllConditions(r => r.CredentialId.ShouldBe(stranded.Successor, "judged dead under the lock, so it moved"), r => r.Status.ShouldBe(RepositoryStatus.Active));
        (await LoadHookAsync(stranded.Hook).ConfigureAwait(false)).CredentialId.ShouldBe(stranded.Successor);
        (await LoadCredentialAsync(stranded.Dead).ConfigureAwait(false)).Status.ShouldBe(CredentialStatus.Active, "the acceptance then landed on the credential, which has nothing bound to it any more");
    }

    [Theory]
    [InlineData(HeldBy.ThisProcess)]
    [InlineData(HeldBy.AnotherPod)]
    public async Task A_credential_whose_lock_is_held_is_skipped_by_the_sweep_and_repaired_on_a_later_pass(HeldBy heldBy)
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var stranded = await SeedStrandedAsync(world, DeadAs.Disconnected).ConfigureAwait(false);

        var write = await HoldLockOfAsync(stranded.Dead, heldBy).ConfigureAwait(false);

        try
        {
            for (var pass = 0; pass < PassesThatWouldSetAFailingCandidateAside; pass++)
                await WithinPatienceAsync(SweepThroughAsync(interceptor: null), "the sweep finished its pass without waiting for the lock", stranded.Dead).ConfigureAwait(false);

            (await LoadRepositoryAsync(stranded.Repository).ConfigureAwait(false)).CredentialId.ShouldBe(stranded.Dead, "somebody is writing the credential's token right now: its verdict cannot be trusted, so nothing moved");
            (await LoadHookAsync(stranded.Hook).ConfigureAwait(false)).CredentialId.ShouldBe(stranded.Dead);
        }
        finally
        {
            await write.DisposeAsync().ConfigureAwait(false);
        }

        await SweepThroughAsync(interceptor: null).ConfigureAwait(false);

        (await LoadRepositoryAsync(stranded.Repository).ConfigureAwait(false)).ShouldSatisfyAllConditions(r => r.CredentialId.ShouldBe(stranded.Successor, "a credential that was busy is repaired once nothing holds it: skipping it was not a failure, however many passes it was skipped"), r => r.Status.ShouldBe(RepositoryStatus.Active));
        (await LoadHookAsync(stranded.Hook).ConfigureAwait(false)).CredentialId.ShouldBe(stranded.Successor);
    }

    [Fact]
    public async Task A_stranded_credential_nobody_is_writing_is_repaired_and_the_lock_is_given_back_for_the_next_re_authorization()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var stranded = await SeedStrandedAsync(world, DeadAs.Refused).ConfigureAwait(false);

        await SweepThroughAsync(interceptor: null).ConfigureAwait(false);

        (await LoadRepositoryAsync(stranded.Repository).ConfigureAwait(false)).CredentialId.ShouldBe(stranded.Successor, "the normal case: nothing holds the credential, so the sweep repairs it as it always did");
        await AssertLockFreeAsync(stranded.Dead).ConfigureAwait(false);

        await WithinPatienceAsync(ReconnectAsync(world, world.NachoId, stranded.Dead, "authz:nacho"), "a re-authorization after the repair took the credential's lock", stranded.Dead).ConfigureAwait(false);

        (await LoadCredentialAsync(stranded.Dead).ConfigureAwait(false)).Status.ShouldBe(CredentialStatus.Active);
        (await LoadRepositoryAsync(stranded.Repository).ConfigureAwait(false)).CredentialId.ShouldBe(stranded.Successor, "and what the repair moved stays moved");
    }

    // ─── The connect-time carry-forward ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_re_authorization_that_lands_after_a_connect_read_the_dead_credential_leaves_its_bindings_on_it()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var stranded = await SeedBoundRefusedCredentialAsync(world).ConfigureAwait(false);
        var init = await InitAsync(world, world.NachoId, replaceCredentialId: null).ConfigureAwait(false);
        var landed = new AfterReadOf(ReadsOwnersCredentials, () => ReconnectAsync(world, world.NachoId, stranded.Dead, "authz:nacho"));

        var connected = await CompleteAsync(world, init, "authz:nacho", Through(landed)).ConfigureAwait(false);

        landed.Ran.ShouldBeTrue("fixture check: the re-authorization landed after the connect read the owner's credentials");
        connected.CredentialId.ShouldNotBe(stranded.Dead);
        (await LoadCredentialAsync(stranded.Dead).ConfigureAwait(false)).Status.ShouldBe(CredentialStatus.Active, "fixture check: the credential is healthy again");
        (await LoadRepositoryAsync(stranded.Repository).ConfigureAwait(false)).ShouldSatisfyAllConditions(r => r.CredentialId.ShouldBe(stranded.Dead, "the connect carried forward a credential that was no longer dead — it must not"), r => r.Status.ShouldBe(RepositoryStatus.Active));
        (await LoadHookAsync(stranded.Hook).ConfigureAwait(false)).CredentialId.ShouldBe(stranded.Dead);
        await AssertNothingBoundAsync(connected.CredentialId!.Value).ConfigureAwait(false);
    }

    [Fact]
    public async Task A_re_authorization_that_arrives_while_a_connect_is_carrying_bindings_forward_waits_for_it_to_commit()
    {
        // A lock held to the end of the request would outlive a rollback and strand every later write of the credential; the row
        // the connect read is held by the transaction instead, which ends with it whichever way it ends.
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var stranded = await SeedBoundRefusedCredentialAsync(world).ConfigureAwait(false);
        var connect = await InitAsync(world, world.NachoId, replaceCredentialId: null).ConfigureAwait(false);
        var reauthorize = await InitAsync(world, world.NachoId, stranded.Dead).ConfigureAwait(false);
        Task<CompleteCredentialOAuthResult>? reconnect = null;
        var finishedBeforeTheCommit = false;
        var arrives = new AfterReadOf(ReadsRepositories, async () =>
        {
            reconnect = Task.Run(() => CompleteAsync(world, reauthorize, "authz:nacho"));
            await Task.Delay(LongEnoughToHaveFinished).ConfigureAwait(false);
            finishedBeforeTheCommit = reconnect.IsCompleted;
        });

        var connected = await CompleteAsync(world, connect, "authz:nacho", Through(arrives)).ConfigureAwait(false);

        arrives.Ran.ShouldBeTrue("fixture check: the re-authorization arrived while the connect was moving the bindings");
        finishedBeforeTheCommit.ShouldBeFalse("the connect holds the credential's row until it commits, so the re-authorization's write has to wait for it");
        await WithinPatienceAsync(reconnect!, "the re-authorization that waited for the commit finished", stranded.Dead).ConfigureAwait(false);

        (await LoadRepositoryAsync(stranded.Repository).ConfigureAwait(false)).ShouldSatisfyAllConditions(r => r.CredentialId.ShouldBe(connected.CredentialId, "the credential was dead when the connect committed, so the move stands"), r => r.Status.ShouldBe(RepositoryStatus.Active));
        (await LoadHookAsync(stranded.Hook).ConfigureAwait(false)).CredentialId.ShouldBe(connected.CredentialId!.Value);
        (await LoadCredentialAsync(stranded.Dead).ConfigureAwait(false)).Status.ShouldBe(CredentialStatus.Active, "the re-authorization then landed on the credential");
    }

    // ─── The departed-member retirement ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(HeldBy.ThisProcess)]
    [InlineData(HeldBy.AnotherPod)]
    public async Task A_departed_members_token_whose_lock_is_held_is_not_retired_until_the_write_is_done(HeldBy heldBy)
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var (credentialId, repositoryId) = await SeedDepartedMembersTokenAsync(world).ConfigureAwait(false);

        var write = await HoldLockOfAsync(credentialId, heldBy).ConfigureAwait(false);

        try
        {
            for (var pass = 0; pass < PassesThatWouldSetAFailingCandidateAside; pass++)
                await WithinPatienceAsync(RetireThroughAsync(interceptor: null), "the retirement finished its pass without waiting for the lock", credentialId).ConfigureAwait(false);

            (await LoadCredentialAsync(credentialId).ConfigureAwait(false)).ShouldSatisfyAllConditions(c => c.EncryptedPayload.ShouldNotBeEmpty("somebody is writing the token right now, and a retirement under it would blank what that write lands"), c => c.DeletedDate.ShouldBeNull(), c => c.Status.ShouldBe(CredentialStatus.Active));
            (await LoadRepositoryAsync(repositoryId).ConfigureAwait(false)).Status.ShouldBe(RepositoryStatus.Active);
        }
        finally
        {
            await write.DisposeAsync().ConfigureAwait(false);
        }

        await RetireThroughAsync(interceptor: null).ConfigureAwait(false);

        (await LoadCredentialAsync(credentialId).ConfigureAwait(false)).ShouldSatisfyAllConditions(c => c.EncryptedPayload.ShouldBeEmpty("a token that was busy is retired once nothing holds it, however many passes it was skipped: skipping it was not a failure"), c => c.DeletedDate.ShouldNotBeNull(), c => c.Status.ShouldBe(CredentialStatus.Revoked));
        (await LoadRepositoryAsync(repositoryId).ConfigureAwait(false)).Status.ShouldBe(RepositoryStatus.Error);
        await AssertLockFreeAsync(credentialId).ConfigureAwait(false);
    }

    [Fact]
    public async Task A_re_authorization_that_arrives_while_a_retirement_is_under_way_is_refused_and_not_reported_done_and_then_undone()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var (credentialId, repositoryId, personId) = await SeedDepartedMembersTokenWithOwnerAsync(world).ConfigureAwait(false);
        gitlab.SeedPersonalAccessToken("departed", "glpat-fresh");
        Task? replace = null;
        var finishedBeforeTheRetirement = false;
        var arrives = new AfterReadOf(ReadsRepositories, async () =>
        {
            await JoinTeamAsync(world, personId).ConfigureAwait(false);
            replace = Task.Run(() => ReplaceTokenAsync(world, personId, credentialId, "glpat-fresh"));
            await Task.Delay(LongEnoughToHaveFinished).ConfigureAwait(false);
            finishedBeforeTheRetirement = replace.IsCompleted;
        });

        await RetireThroughAsync(arrives).ConfigureAwait(false);

        arrives.Ran.ShouldBeTrue("fixture check: the person rejoined and re-authorized after the sweep had judged the token departed and before it saved");
        finishedBeforeTheRetirement.ShouldBeFalse("the retirement holds the token's lock while it saves, so the re-authorization cannot finish inside it");
        await Should.ThrowAsync<CredentialRetiredException>(() => WithinPatienceAsync(replace!, "the re-authorization that waited for the retirement finished", credentialId)).ConfigureAwait(false);

        (await LoadCredentialAsync(credentialId).ConfigureAwait(false)).ShouldSatisfyAllConditions(c => c.EncryptedPayload.ShouldBeEmpty("the retirement stands, and the person was told the re-authorization did not take — not told it did, and then had it blanked"), c => c.DeletedDate.ShouldNotBeNull());
        (await LoadRepositoryAsync(repositoryId).ConfigureAwait(false)).Status.ShouldBe(RepositoryStatus.Error);
    }

    [Fact]
    public async Task A_member_who_rejoined_and_re_authorized_before_the_sweep_took_their_token_keeps_it()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var (credentialId, repositoryId, personId) = await SeedDepartedMembersTokenWithOwnerAsync(world).ConfigureAwait(false);
        gitlab.SeedPersonalAccessToken("departed", "glpat-fresh");
        var rejoins = new AfterReadOf(ReadsDepartedCandidates, async () =>
        {
            await JoinTeamAsync(world, personId).ConfigureAwait(false);
            await ReplaceTokenAsync(world, personId, credentialId, "glpat-fresh").ConfigureAwait(false);
        });

        await RetireThroughAsync(rejoins).ConfigureAwait(false);

        rejoins.Ran.ShouldBeTrue("fixture check: the person rejoined and re-authorized after the sweep had read its candidates");
        (await TokenOfAsync(credentialId).ConfigureAwait(false)).ShouldBe("glpat-fresh", "the sweep judged the token again under its lock, found its person standing in the team, and left the new token alone");
        (await LoadCredentialAsync(credentialId).ConfigureAwait(false)).DeletedDate.ShouldBeNull();
        (await LoadRepositoryAsync(repositoryId).ConfigureAwait(false)).Status.ShouldBe(RepositoryStatus.Active);
    }

    // ─── Plumbing ──────────────────────────────────────────────────────────────────────────────────────────

    private sealed record Stranded(Guid Dead, Guid Successor, Guid Repository, Guid Hook);

    /// <summary>
    /// What a sweep repairs: a dead credential of Nacho's with a repository and a group hook still bound to it, and a live one
    /// of his connected after it to follow — the dead one at the front of the queue, so every pass reaches it first.
    /// </summary>
    private async Task<Stranded> SeedStrandedAsync(World world, DeadAs deadAs)
    {
        var bound = await SeedBoundDeadCredentialAsync(world, deadAs).ConfigureAwait(false);
        var successor = await SeedOAuthCredentialAsync(world, world.NachoId, "at-1", "rt-1").ConfigureAwait(false);

        return new Stranded(bound.Dead, successor, bound.Repository, bound.Hook);
    }

    /// <summary>The same, with nobody connected after it yet: what a plain connect of Nacho's is about to carry forward.</summary>
    private async Task<Stranded> SeedBoundRefusedCredentialAsync(World world) => await SeedBoundDeadCredentialAsync(world, DeadAs.Refused).ConfigureAwait(false);

    private async Task<Stranded> SeedBoundDeadCredentialAsync(World world, DeadAs deadAs)
    {
        world.GitLab.Seed(GitLabDoorkeeperStub.DefaultClientId, "nacho", "at-0", "rt-0");
        var disconnected = deadAs == DeadAs.Disconnected;

        var dead = await SeedOAuthCredentialAsync(world, world.NachoId, "at-0", "rt-0", disconnected: disconnected, status: disconnected ? CredentialStatus.Active : CredentialStatus.Error, lastError: disconnected ? null : "needs reconnect: invalid_grant", id: FrontOfTheQueue()).ConfigureAwait(false);
        await SeedIdentityAsync(world, world.NachoId, dead, "nacho", deleted: disconnected).ConfigureAwait(false);
        var repository = await SeedRepositoryAsync(world, dead, disconnected ? RepositoryStatus.Error : RepositoryStatus.Active).ConfigureAwait(false);
        var hook = await SeedHookAsync(world, dead, RepositoryWebhookRegistrationStatus.DeadLettered).ConfigureAwait(false);

        return new Stranded(dead, Guid.Empty, repository, hook);
    }

    /// <summary>A live personal token of someone who never joined the team — what the retirement sweep is for — with a repository bound through it.</summary>
    private async Task<(Guid Credential, Guid Repository)> SeedDepartedMembersTokenAsync(World world)
    {
        var (credentialId, repositoryId, _) = await SeedDepartedMembersTokenWithOwnerAsync(world).ConfigureAwait(false);

        return (credentialId, repositoryId);
    }

    private async Task<(Guid Credential, Guid Repository, Guid Person)> SeedDepartedMembersTokenWithOwnerAsync(World world)
    {
        var person = await SeedUserAsync().ConfigureAwait(false);
        var credentialId = await SeedTokenCredentialAsync(world, person, "glpat-departed", id: FrontOfTheQueue()).ConfigureAwait(false);
        var repositoryId = await SeedRepositoryAsync(world, credentialId, RepositoryStatus.Active).ConfigureAwait(false);

        return (credentialId, repositoryId, person);
    }

    private async Task JoinTeamAsync(World world, Guid userId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.TeamMembership.Add(Membership(world.TeamId, userId, TeamRole.Member));
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>An id that sorts ahead of any random one: a sweep reads its candidates in id order, so a candidate here is in the first page whatever else the shared database holds.</summary>
    private static Guid FrontOfTheQueue() => Guid.Parse($"00000000-0000-0000-0000-{Guid.NewGuid().ToString("N")[..12]}");

    private async Task SweepThroughAsync(IInterceptor? interceptor)
    {
        using var scope = ScopeThrough(interceptor);

        await scope.Resolve<IMediator>().Send(new RepairDisconnectedCredentialBindingsCommand { BatchSize = 50 }).ConfigureAwait(false);
    }

    private async Task RetireThroughAsync(IInterceptor? interceptor)
    {
        using var scope = ScopeThrough(interceptor);

        await scope.Resolve<IMediator>().Send(new RetireDepartedMembersCredentialsCommand { BatchSize = 50 }).ConfigureAwait(false);
    }

    private ILifetimeScope ScopeThrough(IInterceptor? interceptor) => interceptor == null ? _fixture.BeginScope() : _fixture.BeginScope(Through(interceptor));

    /// <summary>The scope's database commands pass <paramref name="interceptor"/>: production's options with one more interceptor on them.</summary>
    private Action<ContainerBuilder> Through(IInterceptor interceptor) => builder =>
    {
        DbContextOptions<CodeSpaceDbContext> production;

        using (var probe = _fixture.BeginScope())
            production = probe.Resolve<DbContextOptions<CodeSpaceDbContext>>();

        builder.RegisterInstance(new DbContextOptionsBuilder<CodeSpaceDbContext>(production).AddInterceptors(interceptor).Options).As<DbContextOptions<CodeSpaceDbContext>>().SingleInstance();
    };

    /// <summary>What a write of the credential's token holds while it is under way: the in-process semaphore and the advisory lock, or — as another pod's — the advisory lock alone.</summary>
    private async Task<IAsyncDisposable> HoldLockOfAsync(Guid credentialId, HeldBy heldBy)
    {
        using var scope = _fixture.BeginScope();
        var crossProcess = scope.Resolve<ICrossProcessLock>();

        return heldBy == HeldBy.ThisProcess
            ? await CredentialRefreshLock.AcquireAsync(crossProcess, credentialId, CancellationToken.None).ConfigureAwait(false)
            : await crossProcess.AcquireAsync(CredentialRefreshLock.KeyOf(credentialId), CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>What the hourly health probe writes when the provider accepts a credential it had refused: the credential back to Active, on a connection of its own and without the refresh lock. False when the row no longer holds the payload it judged.</summary>
    private async Task<bool> AcceptAgainAsync(Guid credentialId)
    {
        var judged = (await LoadCredentialAsync(credentialId).ConfigureAwait(false)).EncryptedPayload;

        using var scope = _fixture.BeginScope();

        return await scope.Resolve<ICredentialHealthWriter>().MarkValidatedAsync(credentialId, judged, DateTimeOffset.UtcNow, expiry: null, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>The lock was given back: both tiers are taken at once. A lock a sweep leaked would hang every later write of the credential, so this waits only as long as a free lock takes.</summary>
    private async Task AssertLockFreeAsync(Guid credentialId)
    {
        using var scope = _fixture.BeginScope();

        await using var held = await CredentialRefreshLock.AcquireAsync(scope.Resolve<ICrossProcessLock>(), credentialId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }

    private async Task AssertNothingBoundAsync(Guid credentialId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        (await db.Repository.AsNoTracking().CountAsync(r => r.CredentialId == credentialId && r.DeletedDate == null).ConfigureAwait(false)).ShouldBe(0, "nothing was carried onto the other credential");
        (await db.ConnectionWebhook.AsNoTracking().CountAsync(h => h.CredentialId == credentialId).ConfigureAwait(false)).ShouldBe(0);
    }

    // The statements below are the ones that read what a verdict is taken on. They are matched by the tables and clauses that
    // make them what they are, not by position, so a reordering of the service does not move the gap the test makes.

    private static bool ReadsStrandedCandidates(string sql) => Reads(sql, "FROM credential AS", "FROM repository AS", "ORDER BY");

    private static bool ReadsOwnersCredentials(string sql) => Reads(sql, "FROM credential AS", "owner_user_id", "<>");

    private static bool ReadsRepositories(string sql) => Reads(sql, "FROM repository AS");

    private static bool ReadsDepartedCandidates(string sql) => Reads(sql, "FROM credential AS", "team_membership");

    private static bool Reads(string sql, params string[] fragments) => sql.StartsWith("SELECT", StringComparison.Ordinal) && fragments.All(f => sql.Contains(f, StringComparison.Ordinal));

    /// <summary>Runs <c>then</c> once, right after the <c>nth</c> read whose SQL matches — a request landing in the gap that follows the statement that read the candidate.</summary>
    private sealed class AfterReadOf : DbCommandInterceptor
    {
        private readonly Func<string, bool> _matches;
        private readonly Func<Task> _then;
        private readonly int _nth;
        private int _seen;
        private int _ran;

        public AfterReadOf(Func<string, bool> matches, Func<Task> then, int nth = 1)
        {
            _matches = matches;
            _then = then;
            _nth = nth;
        }

        public bool Ran => Volatile.Read(ref _ran) == 1;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!_matches(command.CommandText) || Interlocked.Increment(ref _seen) != _nth) return result;

            await _then().ConfigureAwait(false);
            Volatile.Write(ref _ran, 1);

            return result;
        }
    }
}
