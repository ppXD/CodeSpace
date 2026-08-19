using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Persistence;

/// <summary>
/// Real-Postgres proof that harness execution identity cannot be forged. Every assertion here is a COUNTER-EXAMPLE:
/// the illegal row is offered and the database refuses it, because an invariant that only holds while every writer
/// remembers it is not an invariant. Nothing reads or writes these tables in production yet, so these teeth are the
/// entire contract a later capture slice will build on.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class WorkflowRunHarnessExecutionPersistenceTests
{
    private readonly PostgresFixture _fixture;

    public WorkflowRunHarnessExecutionPersistenceTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Appending_an_attempt_advances_the_execution_head_and_a_finished_execution_terminalizes()
    {
        var world = await SeedWorldAsync();
        var execution = await SeedExecutionAsync(world, generation: 1);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.WorkflowRunHarnessProcessAttempt.Add(Attempt(world, execution, ordinal: 1));
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunHarnessExecution.SingleAsync(candidate => candidate.Id == execution.Id);
            stored.State.ShouldBe(HarnessExecutionState.Running);
            stored.AttemptCount.ShouldBe(1);
            stored.NextAttemptOrdinal.ShouldBe(2);
            stored.Revision.ShouldBe(2);
        }

        // A revise round is the next physical process, not a rewrite of the first one.
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.WorkflowRunHarnessProcessAttempt.Add(Attempt(world, execution, ordinal: 2));
            await db.SaveChangesAsync();
        }

        var exitedAt = DateTimeOffset.UtcNow;
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            foreach (var attempt in await db.WorkflowRunHarnessProcessAttempt.Where(candidate => candidate.ExecutionId == execution.Id).ToListAsync())
            {
                attempt.State = HarnessProcessAttemptState.Exited;
                attempt.ExitCode = 0;
                attempt.ExitedAt = exitedAt;
                attempt.LastObservedAt = exitedAt;
                attempt.LastModifiedAt = exitedAt;
                attempt.Revision++;
            }
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunHarnessExecution.SingleAsync(candidate => candidate.Id == execution.Id);
            stored.AttemptCount.ShouldBe(2);
            stored.State = HarnessExecutionState.Exited;
            stored.TerminalAt = exitedAt;
            stored.LastModifiedAt = exitedAt;
            stored.Revision++;
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunHarnessExecution.SingleAsync(candidate => candidate.Id == execution.Id);
            stored.State.ShouldBe(HarnessExecutionState.Exited);
            stored.AttemptCount.ShouldBe(2);
            stored.NextAttemptOrdinal.ShouldBe(3);
            (await db.WorkflowRunHarnessProcessAttempt.CountAsync(candidate => candidate.ExecutionId == execution.Id)).ShouldBe(2);
        }
    }

    [Fact]
    public async Task Generations_start_at_one_are_contiguous_and_cannot_open_over_a_live_predecessor()
    {
        var world = await SeedWorldAsync();

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.WorkflowRunHarnessExecution.Add(Execution(world, generation: 2));
            var firstGeneration = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            firstGeneration.InnerException?.Message.ShouldContain("generations are contiguous from one");
        }

        var live = await SeedExecutionAsync(world, generation: 1);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.WorkflowRunHarnessExecution.Add(Execution(world, generation: 2));
            var overLive = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            overLive.InnerException?.Message.ShouldContain("cannot open a generation while its predecessor is live");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.WorkflowRunHarnessExecution.Add(Execution(world, generation: 3));
            var skipped = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            skipped.InnerException?.Message.ShouldContain("generations are contiguous from one");
        }

        await AbandonAsync(world, live);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.WorkflowRunHarnessExecution.Add(Execution(world, generation: 2));
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            (await db.WorkflowRunHarnessExecution.CountAsync(candidate => candidate.AgentRunId == world.AgentRunId)).ShouldBe(2);
        }
    }

    [Fact]
    public async Task Execution_is_born_unclaimed_pending_and_must_mirror_its_agent_run_scope()
    {
        var world = await SeedWorldAsync();

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var orphan = Execution(world, generation: 1);
            orphan.AgentRunId = Guid.NewGuid();
            db.WorkflowRunHarnessExecution.Add(orphan);
            var missingRun = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            missingRun.InnerException?.Message.ShouldContain("requires its tenant-bound AgentRun");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var borrowed = Execution(world, generation: 1);
            borrowed.WorkflowRunId = Guid.NewGuid();   // the seeded AgentRun is standalone
            db.WorkflowRunHarnessExecution.Add(borrowed);
            var mismatch = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            mismatch.InnerException?.Message.ShouldContain("must mirror its AgentRun workflow run exactly");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var preClaimed = Execution(world, generation: 1);
            preClaimed.State = HarnessExecutionState.Running;
            preClaimed.LeaseOwnerId = world.ActorId;
            preClaimed.LeaseFence = 1;
            preClaimed.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
            db.WorkflowRunHarnessExecution.Add(preClaimed);
            var preClaim = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            preClaim.InnerException?.Message.ShouldContain("must start as an unclaimed empty Pending revision-one generation");
        }
    }

    /// <summary>A standalone AgentRun has no workflow run, and one spawned by a node has exactly its own — this is why execution identity is AgentRun-keyed rather than workflow-run-keyed.</summary>
    [Fact]
    public async Task Execution_records_a_standalone_run_with_no_workflow_run_and_a_spawned_run_with_its_own()
    {
        var standalone = await SeedWorldAsync();
        var spawnedWorkflowRunId = Guid.NewGuid();
        var spawned = await SeedWorldAsync(spawnedWorkflowRunId);

        await SeedExecutionAsync(standalone, generation: 1);
        await SeedExecutionAsync(spawned, generation: 1, workflowRunId: spawnedWorkflowRunId);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowRunHarnessExecution.SingleAsync(candidate => candidate.AgentRunId == standalone.AgentRunId)).WorkflowRunId.ShouldBeNull();
        (await db.WorkflowRunHarnessExecution.SingleAsync(candidate => candidate.AgentRunId == spawned.AgentRunId)).WorkflowRunId.ShouldBe(spawnedWorkflowRunId);
    }

    [Fact]
    public async Task Execution_identity_revision_and_terminal_state_are_immutable_and_the_row_cannot_be_deleted()
    {
        var world = await SeedWorldAsync();
        var execution = await SeedExecutionAsync(world, generation: 1);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunHarnessExecution.SingleAsync(candidate => candidate.Id == execution.Id);
            stored.RunnerKind = "kubernetes";
            stored.Revision++;
            stored.LastModifiedAt = DateTimeOffset.UtcNow;
            var rebrand = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            rebrand.InnerException?.Message.ShouldContain("stable execution identity is immutable");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunHarnessExecution.SingleAsync(candidate => candidate.Id == execution.Id);
            stored.LastModifiedAt = DateTimeOffset.UtcNow;   // a mutable column, but silently — no revision to compare against
            var noRevision = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            noRevision.InnerException?.Message.ShouldContain("revision must advance exactly once and time must not rewind");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var deletion = await Should.ThrowAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM workflow_run_harness_execution WHERE id = {execution.Id}"));
            deletion.Message.ShouldContain("DELETE rejected");
        }

        await AbandonAsync(world, execution);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunHarnessExecution.SingleAsync(candidate => candidate.Id == execution.Id);
            stored.State = HarnessExecutionState.Pending;
            stored.TerminalAt = null;
            stored.ErrorCode = null;
            stored.ErrorMessage = null;
            stored.Revision++;
            stored.LastModifiedAt = DateTimeOffset.UtcNow;
            var revive = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            revive.InnerException?.Message.ShouldContain("terminal state is immutable");
        }
    }

    [Fact]
    public async Task Execution_lease_advances_once_is_state_neutral_and_is_released_at_terminal()
    {
        var world = await SeedWorldAsync();
        var execution = await SeedExecutionAsync(world, generation: 1);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunHarnessExecution.SingleAsync(candidate => candidate.Id == execution.Id);
            stored.LeaseOwnerId = world.ActorId;
            stored.LeaseFence = 3;
            stored.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
            stored.Revision++;
            stored.LastModifiedAt = DateTimeOffset.UtcNow;
            var jumped = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            jumped.InnerException?.Message.ShouldContain("lease claim must advance the fence exactly once with a live expiry");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunHarnessExecution.SingleAsync(candidate => candidate.Id == execution.Id);
            stored.LeaseOwnerId = world.ActorId;
            stored.LeaseFence = 1;
            stored.LeaseExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            stored.Revision++;
            stored.LastModifiedAt = DateTimeOffset.UtcNow;
            var expired = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            expired.InnerException?.Message.ShouldContain("lease claim must advance the fence exactly once with a live expiry");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunHarnessExecution.SingleAsync(candidate => candidate.Id == execution.Id);
            stored.LeaseOwnerId = world.ActorId;
            stored.LeaseFence = 1;
            stored.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
            stored.AttemptCount = 1;
            stored.NextAttemptOrdinal = 2;
            stored.Revision++;
            stored.LastModifiedAt = DateTimeOffset.UtcNow;
            var smuggled = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            smuggled.InnerException?.Message.ShouldContain("lease claim cannot mutate execution state");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunHarnessExecution.SingleAsync(candidate => candidate.Id == execution.Id);
            stored.LeaseOwnerId = world.ActorId;
            stored.LeaseFence = 1;
            stored.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
            stored.Revision++;
            stored.LastModifiedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunHarnessExecution.SingleAsync(candidate => candidate.Id == execution.Id);
            stored.LeaseOwnerId = Guid.NewGuid();
            stored.LeaseFence = 2;
            stored.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
            stored.Revision++;
            stored.LastModifiedAt = DateTimeOffset.UtcNow;
            var stolen = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            stolen.InnerException?.Message.ShouldContain("live lease cannot be reclaimed");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var terminalAt = DateTimeOffset.UtcNow;
            var stored = await db.WorkflowRunHarnessExecution.SingleAsync(candidate => candidate.Id == execution.Id);
            stored.State = HarnessExecutionState.Abandoned;
            stored.TerminalAt = terminalAt;
            stored.ErrorCode = "runner.host-gone";
            stored.ErrorMessage = "the runner host never came back";
            stored.Revision++;
            stored.LastModifiedAt = terminalAt;
            var heldLease = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            heldLease.InnerException?.Message.ShouldContain("ck_workflow_run_harness_execution_terminal_lease");
        }

        // The unheld lease is the ONLY difference — releasing it is what makes the same terminal write legal, and it
        // has to be its OWN statement, because a terminal write that evicts a still-live lease is refused.
        (await WriteLeaseAsync(execution, owner: null, fence: 1, expiresAt: null)).ShouldBeEmpty();
        await AbandonAsync(world, execution);

        using (var scope = _fixture.BeginScope())
        {
            var stored = await scope.Resolve<CodeSpaceDbContext>().WorkflowRunHarnessExecution.SingleAsync(candidate => candidate.Id == execution.Id);
            stored.State.ShouldBe(HarnessExecutionState.Abandoned);
            stored.LeaseOwnerId.ShouldBeNull();
            stored.LeaseFence.ShouldBe(1, customMessage: "releasing a lease must not rewind the fence a later writer compares against");
        }
    }

    /// <summary>
    /// The fence arm cannot key on the FENCE alone. An owner swap that leaves lease_fence untouched never enters a
    /// fence-only arm, so a live lease is simply taken; and the fence a released lease left behind must not be
    /// re-usable, or two owners hold one fence value and a resurrected observer is indistinguishable from the holder.
    /// The renewal case is why the arm cannot key on the owner alone either: a holder extending its own expiry has to
    /// stay legal, or renewal is only expressible through the very statement shape theft uses.
    /// </summary>
    [Fact]
    public async Task No_statement_shape_reassigns_a_live_lease_and_a_same_owner_renewal_still_succeeds()
    {
        var world = await SeedWorldAsync();
        var execution = await SeedExecutionAsync(world, generation: 1);
        var other = Guid.NewGuid();
        var live = DateTimeOffset.UtcNow.AddMinutes(5);

        (await WriteLeaseAsync(execution, world.ActorId, fence: 1, live)).ShouldBeEmpty(customMessage: "the first claim advances the fence from zero to one");
        (await WriteLeaseAsync(execution, other, fence: 1, live)).ShouldContain("live lease cannot be reclaimed",
            customMessage: "an owner swap that never touches lease_fence must not be a way past the fence arm");

        (await WriteLeaseAsync(execution, world.ActorId, fence: 1, live.AddMinutes(5))).ShouldBeEmpty(
            customMessage: "the holder must be able to extend its own expiry, or renewal has no legal shape of its own");
        (await WriteLeaseAsync(execution, world.ActorId, fence: 2, live.AddMinutes(5))).ShouldContain("live lease cannot be reclaimed",
            customMessage: "re-fencing a lease that is still live invalidates only its own holder, so it is never a renewal");

        (await WriteLeaseAsync(execution, owner: null, fence: 1, expiresAt: null)).ShouldBeEmpty(customMessage: "a holder may hand the lease back");
        (await WriteLeaseAsync(execution, other, fence: 1, live)).ShouldContain("must advance the fence exactly once",
            customMessage: "two owners must never acquire the same fence value");
        (await WriteLeaseAsync(execution, other, fence: 2, live)).ShouldBeEmpty(customMessage: "the next acquisition takes the next fence");
        (await WriteLeaseAsync(execution, world.ActorId, fence: 2, live)).ShouldContain("live lease cannot be reclaimed",
            customMessage: "the displaced holder must not be able to write itself back in");
    }

    /// <summary>Same hole on the attempt's observer claim, including the renewal the guarded path refuses.</summary>
    [Fact]
    public async Task No_statement_shape_reassigns_a_live_attempt_claim_and_a_same_owner_renewal_still_succeeds()
    {
        var world = await SeedWorldAsync();
        var attempt = await SeedAttemptAsync(world, await SeedExecutionAsync(world, generation: 1), ordinal: 1);
        var other = Guid.NewGuid();
        var live = DateTimeOffset.UtcNow.AddMinutes(5);

        (await WriteClaimAsync(attempt, world.ActorId, fence: 1, live)).ShouldBeEmpty(customMessage: "the first claim advances the fence from zero to one");
        (await WriteClaimAsync(attempt, other, fence: 1, live)).ShouldContain("live claim cannot be stolen",
            customMessage: "an observer swap that never touches claim_fence must not be a way past the fence arm");

        (await WriteClaimAsync(attempt, world.ActorId, fence: 1, live.AddMinutes(5))).ShouldBeEmpty(
            customMessage: "the observer must be able to extend its own claim, or renewal has no legal shape of its own");

        (await WriteClaimAsync(attempt, owner: null, fence: 1, expiresAt: null)).ShouldBeEmpty(customMessage: "an observer may hand the claim back");
        (await WriteClaimAsync(attempt, other, fence: 1, live)).ShouldContain("must advance the fence exactly once",
            customMessage: "two observers must never acquire the same claim fence value");
        (await WriteClaimAsync(attempt, other, fence: 2, live)).ShouldBeEmpty(customMessage: "the next acquisition takes the next fence");
    }

    /// <summary>
    /// ck_..._terminal_lease demands a terminal execution hold no lease — which a single statement can satisfy BY
    /// nulling the live lease it is evicting. That is how a third party closed an execution out from under its holder
    /// and stamped its own error code on it. Releasing has to be its own revision, which is what the displaced
    /// holder's <c>WHERE revision = @observed</c> can detect.
    /// </summary>
    [Fact]
    public async Task A_live_lease_is_not_evictable_by_the_statement_that_closes_the_execution()
    {
        var world = await SeedWorldAsync();
        var execution = await SeedExecutionAsync(world, generation: 1);

        (await WriteLeaseAsync(execution, world.ActorId, fence: 1, DateTimeOffset.UtcNow.AddMinutes(5))).ShouldBeEmpty();

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var terminalAt = DateTimeOffset.UtcNow;
            var stored = await db.WorkflowRunHarnessExecution.SingleAsync(candidate => candidate.Id == execution.Id);
            stored.State = HarnessExecutionState.Abandoned;
            stored.TerminalAt = terminalAt;
            stored.ErrorCode = "third-party.says-gone";
            stored.ErrorMessage = "closed by an observer that never held the lease";
            stored.LeaseOwnerId = null;
            stored.LeaseExpiresAt = null;
            stored.Revision++;
            stored.LastModifiedAt = terminalAt;
            var evicted = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            evicted.InnerException?.Message.ShouldContain("live lease must be released before its execution is closed");
        }

        (await WriteLeaseAsync(execution, owner: null, fence: 1, expiresAt: null)).ShouldBeEmpty();
        await AbandonAsync(world, execution);

        using (var scope = _fixture.BeginScope())
        {
            var stored = await scope.Resolve<CodeSpaceDbContext>().WorkflowRunHarnessExecution.SingleAsync(candidate => candidate.Id == execution.Id);
            stored.State.ShouldBe(HarnessExecutionState.Abandoned);
            stored.ErrorCode.ShouldBe("runner.host-gone", customMessage: "the evictor's reason must never have landed on the row");
        }
    }

    /// <summary>
    /// The resurrected-observer path: a worker whose Agent Run fence was superseded writes Lost plus its own reason
    /// over the live claim of the observer that replaced it, freezing the attempt terminal forever — after which the
    /// execution's own terminalize gate sees no Running attempt and lets anyone close it. Gated on the CLAIM rather
    /// than on worker_fence_epoch, which is immutable here and so could never equal a bumped Agent Run fence again.
    /// </summary>
    [Fact]
    public async Task A_live_attempt_claim_is_not_evictable_by_the_statement_that_records_the_outcome()
    {
        var world = await SeedWorldAsync();
        var attempt = await SeedAttemptAsync(world, await SeedExecutionAsync(world, generation: 1), ordinal: 1);

        (await WriteClaimAsync(attempt, world.ActorId, fence: 1, DateTimeOffset.UtcNow.AddMinutes(5))).ShouldBeEmpty();

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var lostAt = DateTimeOffset.UtcNow;
            var stored = await db.WorkflowRunHarnessProcessAttempt.SingleAsync(candidate => candidate.Id == attempt.Id);
            stored.State = HarnessProcessAttemptState.Lost;
            stored.ExitedAt = lostAt;
            stored.ErrorCode = "resurrected.says-gone";
            stored.ErrorMessage = "declared lost by a worker the run had already fenced out";
            stored.ClaimOwnerId = null;
            stored.ClaimExpiresAt = null;
            stored.LastObservedAt = lostAt;
            stored.Revision++;
            stored.LastModifiedAt = lostAt;
            var evicted = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            evicted.InnerException?.Message.ShouldContain("live claim must be released before its process outcome is recorded");
        }

        (await WriteClaimAsync(attempt, owner: null, fence: 1, expiresAt: null)).ShouldBeEmpty();
        await LoseAsync(attempt);

        using (var scope = _fixture.BeginScope())
        {
            var stored = await scope.Resolve<CodeSpaceDbContext>().WorkflowRunHarnessProcessAttempt.SingleAsync(candidate => candidate.Id == attempt.Id);
            stored.ErrorCode.ShouldBe("runner.process-gone", customMessage: "the evictor's reason must never have landed on the row");
        }
    }

    /// <summary>
    /// The obligation the generation gate imposes on its first writer, pinned so it is discovered here rather than in
    /// production: a launch that died between writing this row and inserting attempt 1 leaves a Pending generation
    /// that blocks every later one, cannot be closed as a clean Exited because nothing ever ran, and is invisible to
    /// the lease-expiry index because it never held a lease. Only an Abandoned write with a reason unblocks the run.
    /// </summary>
    [Fact]
    public async Task A_never_attempted_pending_generation_blocks_relaunch_until_an_age_scan_abandons_it()
    {
        var world = await SeedWorldAsync();
        var stillborn = await SeedExecutionAsync(world, generation: 1);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.WorkflowRunHarnessExecution.Add(Execution(world, generation: 2));
            var blocked = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            blocked.InnerException?.Message.ShouldContain("cannot open a generation while its predecessor is live");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var terminalAt = DateTimeOffset.UtcNow;
            var stored = await db.WorkflowRunHarnessExecution.SingleAsync(candidate => candidate.Id == stillborn.Id);
            stored.State = HarnessExecutionState.Exited;
            stored.TerminalAt = terminalAt;
            stored.Revision++;
            stored.LastModifiedAt = terminalAt;
            var cleanExit = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            cleanExit.InnerException?.Message.ShouldContain("ck_workflow_run_harness_execution_terminal",
                customMessage: "a generation that never ran a process must not be closable as a clean Exited");
        }

        using (var scope = _fixture.BeginScope())
        {
            var live = scope.Resolve<CodeSpaceDbContext>().WorkflowRunHarnessExecution.Where(candidate => candidate.TeamId == world.TeamId
                && (candidate.State == HarnessExecutionState.Pending || candidate.State == HarnessExecutionState.Running));
            (await live.CountAsync(candidate => candidate.LeaseExpiresAt < DateTimeOffset.UtcNow)).ShouldBe(0,
                customMessage: "an expiry-driven reaper cannot find it: lease_expires_at is NULL by birth, which is why ix_..._stale_live exists");
            (await live.CountAsync(candidate => candidate.LastModifiedAt <= DateTimeOffset.UtcNow)).ShouldBe(1,
                customMessage: "the age predicate is the only one that finds it — without it the Agent Run is wedged with no way out");
        }

        await AbandonAsync(world, stillborn);

        using var reopened = _fixture.BeginScope();
        var reopenedDb = reopened.Resolve<CodeSpaceDbContext>();
        reopenedDb.WorkflowRunHarnessExecution.Add(Execution(world, generation: 2));
        await reopenedDb.SaveChangesAsync();
    }

    [Fact]
    public async Task Execution_head_cannot_move_without_its_attempt_and_cannot_terminalize_over_a_live_one()
    {
        var world = await SeedWorldAsync();
        var execution = await SeedExecutionAsync(world, generation: 1);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunHarnessExecution.SingleAsync(candidate => candidate.Id == execution.Id);
            stored.State = HarnessExecutionState.Running;
            stored.AttemptCount = 1;
            stored.NextAttemptOrdinal = 2;
            stored.Revision++;
            stored.LastModifiedAt = DateTimeOffset.UtcNow;
            var phantom = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            phantom.InnerException?.Message.ShouldContain("head advance requires its exact appended attempt");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunHarnessExecution.SingleAsync(candidate => candidate.Id == execution.Id);
            stored.State = HarnessExecutionState.Running;
            stored.AttemptCount = 2;
            stored.NextAttemptOrdinal = 3;
            stored.Revision++;
            stored.LastModifiedAt = DateTimeOffset.UtcNow;
            var doubled = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            doubled.InnerException?.Message.ShouldContain("attempt-head advances are exactly one live attempt");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunHarnessExecution.SingleAsync(candidate => candidate.Id == execution.Id);
            stored.State = HarnessExecutionState.Running;
            stored.Revision++;
            stored.LastModifiedAt = DateTimeOffset.UtcNow;
            var declaredLive = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            declaredLive.InnerException?.Message.ShouldContain("illegal state transition");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.WorkflowRunHarnessProcessAttempt.Add(Attempt(world, execution, ordinal: 1));
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var terminalAt = DateTimeOffset.UtcNow;
            var stored = await db.WorkflowRunHarnessExecution.SingleAsync(candidate => candidate.Id == execution.Id);
            stored.State = HarnessExecutionState.Exited;
            stored.TerminalAt = terminalAt;
            stored.Revision++;
            stored.LastModifiedAt = terminalAt;
            var overLive = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            overLive.InnerException?.Message.ShouldContain("cannot terminalize while an attempt is still running");
        }
    }

    [Fact]
    public async Task Execution_column_checks_reject_a_forged_runner_identity_deadline_lease_and_blank_error()
    {
        var world = await SeedWorldAsync();

        await RejectsExecutionAsync(world, "ck_workflow_run_harness_execution_identity", execution => execution.RunnerKind = "Kubernetes/Pod");
        await RejectsExecutionAsync(world, "ck_workflow_run_harness_execution_identity", execution => execution.HarnessTypeKey = "codex-cli");
        await RejectsExecutionAsync(world, "ck_workflow_run_harness_execution_head", execution => execution.RunnerLocatorSchemaVersion = 0);
        await RejectsExecutionAsync(world, "ck_workflow_run_harness_execution_time", execution => execution.DeadlineAt = execution.CreatedAt.AddSeconds(-1));

        var execution = await SeedExecutionAsync(world, generation: 1);

        // An owner arriving with no fence is now refused by the GUARD, because the claim arm enters on the owner axis
        // too — the CHECK behind it is what still catches a lease that names nobody but looks live to a reaper.
        (await WriteLeaseAsync(execution, world.ActorId, fence: 0, DateTimeOffset.UtcNow.AddMinutes(5)))
            .ShouldContain("lease claim must advance the fence exactly once");

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunHarnessExecution.SingleAsync(candidate => candidate.Id == execution.Id);
            stored.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
            stored.Revision++;
            stored.LastModifiedAt = DateTimeOffset.UtcNow;
            var ownerless = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            ownerless.InnerException?.Message.ShouldContain("ck_workflow_run_harness_execution_lease");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var terminalAt = DateTimeOffset.UtcNow;
            var stored = await db.WorkflowRunHarnessExecution.SingleAsync(candidate => candidate.Id == execution.Id);
            stored.State = HarnessExecutionState.Abandoned;
            stored.TerminalAt = terminalAt;
            stored.ErrorCode = "   ";
            stored.ErrorMessage = "the reason nobody can grep for";
            stored.Revision++;
            stored.LastModifiedAt = terminalAt;
            var blankReason = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            blankReason.InnerException?.Message.ShouldContain("ck_workflow_run_harness_execution_error");
        }
    }

    [Fact]
    public async Task Attempts_are_contiguous_from_one_and_need_the_current_worker_fence_and_a_live_execution()
    {
        var world = await SeedWorldAsync();
        var execution = await SeedExecutionAsync(world, generation: 1);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stale = Attempt(world, execution, ordinal: 1);
            stale.WorkerFenceEpoch = world.FenceEpoch - 1;
            db.WorkflowRunHarnessProcessAttempt.Add(stale);
            var staleFence = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            staleFence.InnerException?.Message.ShouldContain("stale worker fence rejected");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.WorkflowRunHarnessProcessAttempt.Add(Attempt(world, execution, ordinal: 2));
            var gap = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            gap.InnerException?.Message.ShouldContain("ordinals are contiguous from one");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var preTerminal = Attempt(world, execution, ordinal: 1);
            preTerminal.State = HarnessProcessAttemptState.Exited;
            preTerminal.ExitCode = 0;
            preTerminal.ExitedAt = preTerminal.CreatedAt;
            db.WorkflowRunHarnessProcessAttempt.Add(preTerminal);
            var bornDead = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            bornDead.InnerException?.Message.ShouldContain("must start as an unclaimed Running revision-one process");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunHarnessExecution.SingleAsync(candidate => candidate.Id == execution.Id);
            stored.AttemptCount.ShouldBe(0, customMessage: "a refused attempt must not have moved the head");
            stored.Revision.ShouldBe(1);
        }

        await AbandonAsync(world, execution);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.WorkflowRunHarnessProcessAttempt.Add(Attempt(world, execution, ordinal: 1));
            var afterTerminal = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            afterTerminal.InnerException?.Message.ShouldContain("requires its live tenant-bound execution");
        }
    }

    [Fact]
    public async Task Attempt_identity_observation_and_terminal_state_are_immutable_and_the_row_cannot_be_deleted()
    {
        var world = await SeedWorldAsync();
        var execution = await SeedExecutionAsync(world, generation: 1);
        var attempt = await SeedAttemptAsync(world, execution, ordinal: 1);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunHarnessProcessAttempt.SingleAsync(candidate => candidate.Id == attempt.Id);
            stored.RunnerLocatorJson = "{\"pid\": 99999}";
            stored.Revision++;
            stored.LastModifiedAt = DateTimeOffset.UtcNow;
            var relocated = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            relocated.InnerException?.Message.ShouldContain("stable process identity is immutable");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunHarnessProcessAttempt.SingleAsync(candidate => candidate.Id == attempt.Id);
            stored.CheckpointRef = "offset:512";
            stored.LastObservedAt = stored.LastObservedAt.AddMinutes(-1);
            stored.Revision++;
            stored.LastModifiedAt = DateTimeOffset.UtcNow;
            var rewound = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            rewound.InnerException?.Message.ShouldContain("revision/observation must advance monotonically");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var deletion = await Should.ThrowAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM workflow_run_harness_process_attempt WHERE id = {attempt.Id}"));
            deletion.Message.ShouldContain("DELETE rejected");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunHarnessProcessAttempt.SingleAsync(candidate => candidate.Id == attempt.Id);
            stored.State = HarnessProcessAttemptState.Exited;
            stored.Revision++;
            stored.LastModifiedAt = DateTimeOffset.UtcNow;
            var noMarker = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            noMarker.InnerException?.Message.ShouldContain("illegal process outcome");
        }

        await LoseAsync(attempt);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunHarnessProcessAttempt.SingleAsync(candidate => candidate.Id == attempt.Id);
            stored.State = HarnessProcessAttemptState.Exited;
            stored.ExitCode = 0;
            stored.Revision++;
            stored.LastModifiedAt = DateTimeOffset.UtcNow;
            var upgraded = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            upgraded.InnerException?.Message.ShouldContain("terminal state is immutable");
        }
    }

    [Fact]
    public async Task Attempt_claim_is_fenced_state_neutral_and_impossible_once_terminal()
    {
        var world = await SeedWorldAsync();
        var execution = await SeedExecutionAsync(world, generation: 1);
        var attempt = await SeedAttemptAsync(world, execution, ordinal: 1);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunHarnessProcessAttempt.SingleAsync(candidate => candidate.Id == attempt.Id);
            stored.ClaimOwnerId = world.ActorId;
            stored.ClaimFence = 4;
            stored.ClaimExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
            stored.Revision++;
            stored.LastModifiedAt = DateTimeOffset.UtcNow;
            var jumped = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            jumped.InnerException?.Message.ShouldContain("claim must advance the fence exactly once with a live expiry");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var exitedAt = DateTimeOffset.UtcNow;
            var stored = await db.WorkflowRunHarnessProcessAttempt.SingleAsync(candidate => candidate.Id == attempt.Id);
            stored.State = HarnessProcessAttemptState.Exited;
            stored.ExitCode = 0;
            stored.ExitedAt = exitedAt;
            stored.ClaimOwnerId = world.ActorId;
            stored.ClaimFence = 1;
            stored.ClaimExpiresAt = exitedAt.AddMinutes(5);
            stored.LastObservedAt = exitedAt;
            stored.Revision++;
            stored.LastModifiedAt = exitedAt;
            var claimedDead = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            claimedDead.InnerException?.Message.ShouldContain("cannot be claimed once terminal");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunHarnessProcessAttempt.SingleAsync(candidate => candidate.Id == attempt.Id);
            stored.ClaimOwnerId = world.ActorId;
            stored.ClaimFence = 1;
            stored.ClaimExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
            stored.CheckpointRef = "offset:4096";
            stored.Revision++;
            stored.LastModifiedAt = DateTimeOffset.UtcNow;
            var smuggled = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            smuggled.InnerException?.Message.ShouldContain("claim cannot mutate observed process state");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunHarnessProcessAttempt.SingleAsync(candidate => candidate.Id == attempt.Id);
            stored.ClaimOwnerId = world.ActorId;
            stored.ClaimFence = 1;
            stored.ClaimExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
            stored.Revision++;
            stored.LastModifiedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var stored = await db.WorkflowRunHarnessProcessAttempt.SingleAsync(candidate => candidate.Id == attempt.Id);
            stored.ClaimOwnerId = Guid.NewGuid();
            stored.ClaimFence = 2;
            stored.ClaimExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
            stored.Revision++;
            stored.LastModifiedAt = DateTimeOffset.UtcNow;
            var stolen = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            stolen.InnerException?.Message.ShouldContain("live claim cannot be stolen");
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var exitedAt = DateTimeOffset.UtcNow;
            var stored = await db.WorkflowRunHarnessProcessAttempt.SingleAsync(candidate => candidate.Id == attempt.Id);
            stored.State = HarnessProcessAttemptState.Exited;
            stored.ExitCode = 0;
            stored.ExitedAt = exitedAt;
            stored.LastObservedAt = exitedAt;
            stored.Revision++;
            stored.LastModifiedAt = exitedAt;
            var heldClaim = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
            heldClaim.InnerException?.Message.ShouldContain("ck_workflow_run_harness_process_attempt_terminal_claim");
        }
    }

    [Fact]
    public async Task Attempt_column_checks_reject_a_non_object_locator_a_backwards_clock_and_a_blank_reason()
    {
        var world = await SeedWorldAsync();
        var execution = await SeedExecutionAsync(world, generation: 1);

        await RejectsAttemptAsync(world, execution, "ck_workflow_run_harness_process_attempt_locator", attempt => attempt.RunnerLocatorJson = "[]");
        await RejectsAttemptAsync(world, execution, "ck_workflow_run_harness_process_attempt_locator", attempt => attempt.CheckpointRef = "   ");
        await RejectsAttemptAsync(world, execution, "ck_workflow_run_harness_process_attempt_time", attempt => attempt.LastObservedAt = attempt.StartedAt.AddSeconds(-1));

        var attempt = await SeedAttemptAsync(world, execution, ordinal: 1);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var lostAt = DateTimeOffset.UtcNow;
        var stored = await db.WorkflowRunHarnessProcessAttempt.SingleAsync(candidate => candidate.Id == attempt.Id);
        stored.State = HarnessProcessAttemptState.Lost;
        stored.ExitedAt = lostAt;
        stored.LastObservedAt = lostAt;
        stored.ErrorCode = "  ";
        stored.ErrorMessage = "the reason nobody can grep for";
        stored.Revision++;
        stored.LastModifiedAt = lostAt;
        var blankReason = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
        blankReason.InnerException?.Message.ShouldContain("ck_workflow_run_harness_process_attempt_error");
    }

    /// <summary>
    /// The two unique indexes are the CONCURRENCY backstop: in one session the guard rejects a duplicate generation
    /// or ordinal first, but two workers racing past their own snapshots see no conflict, and only the index does.
    /// So the counter-example here is the index's own existence and uniqueness — asserting a duplicate insert throws
    /// would prove the trigger, not the index it exists to back.
    /// </summary>
    [Theory]
    [InlineData("ux_workflow_run_harness_execution_generation", "workflow_run_harness_execution", "(team_id, agent_run_id, generation)")]
    [InlineData("ux_workflow_run_harness_process_attempt_ordinal", "workflow_run_harness_process_attempt", "(team_id, execution_id, attempt_ordinal)")]
    public async Task Concurrency_backstop_index_is_installed_and_unique(string indexName, string tableName, string expectedColumns)
    {
        var definitions = await IndexDefinitionsAsync(tableName, indexName);

        definitions.ShouldHaveSingleItem(
            customMessage: $"index '{indexName}' must exist after 0137 applies — without it two racing workers each pass the trigger against their own snapshot. Diagnose with: psql -c '\\di {indexName}'.");
        definitions[0].ShouldStartWith("CREATE UNIQUE",
            customMessage: $"index '{indexName}' exists but is not UNIQUE, so it rejects nothing.");
        definitions[0].ShouldContain(expectedColumns,
            customMessage: $"index '{indexName}' is unique over the wrong columns, so the race it exists to lose stays winnable. Diagnose with: psql -c '\\d {tableName}'.");
    }

    /// <summary>
    /// The reaper's only way in. A Pending generation that never ran has lease_expires_at NULL, so
    /// ix_..._lease_expiry never returns it — and while it is unfound its Agent Run can open no further generation.
    /// Pinned by COLUMNS, because an index of the right name on the wrong columns would find nothing either.
    /// </summary>
    [Fact]
    public async Task Stale_live_age_index_can_find_a_generation_that_never_held_a_lease()
    {
        var definitions = await IndexDefinitionsAsync("workflow_run_harness_execution", "ix_workflow_run_harness_execution_stale_live");

        definitions.ShouldHaveSingleItem(
            customMessage: "ix_workflow_run_harness_execution_stale_live must exist after 0137 applies — it is the only index that can find a never-claimed Pending generation, which otherwise blocks every re-launch of its Agent Run forever.");
        definitions[0].ShouldContain("(last_modified_at, team_id, id)",
            customMessage: "the age scan must lead on last_modified_at with no team prefix, so one reaper sweep covers every tenant.");
        definitions[0].ShouldContain("WHERE",
            customMessage: "the index must stay partial to the live states, or it grows with every closed execution the reaper will never look at.");
    }

    private async Task<IReadOnlyList<string>> IndexDefinitionsAsync(string tableName, string indexName)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename = @table AND indexname = @index", connection);
        command.Parameters.AddWithValue("table", tableName);
        command.Parameters.AddWithValue("index", indexName);
        await using var reader = await command.ExecuteReaderAsync();
        var definitions = new List<string>();
        while (await reader.ReadAsync()) definitions.Add(reader.GetString(0));
        return definitions;
    }

    /// <summary>Offers one lease write and reports the database's refusal, or empty when it was accepted — so a whole
    /// table of legal and illegal statement SHAPES reads as one line each instead of one scope each.</summary>
    private async Task<string> WriteLeaseAsync(WorkflowRunHarnessExecution execution, Guid? owner, long fence, DateTimeOffset? expiresAt)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var stored = await db.WorkflowRunHarnessExecution.SingleAsync(candidate => candidate.Id == execution.Id);
        stored.LeaseOwnerId = owner;
        stored.LeaseFence = fence;
        stored.LeaseExpiresAt = expiresAt;
        stored.Revision++;
        stored.LastModifiedAt = DateTimeOffset.UtcNow;

        try
        {
            await db.SaveChangesAsync();
            return string.Empty;
        }
        catch (DbUpdateException refused)
        {
            return refused.InnerException?.Message ?? refused.Message;
        }
    }

    private async Task<string> WriteClaimAsync(WorkflowRunHarnessProcessAttempt attempt, Guid? owner, long fence, DateTimeOffset? expiresAt)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var stored = await db.WorkflowRunHarnessProcessAttempt.SingleAsync(candidate => candidate.Id == attempt.Id);
        stored.ClaimOwnerId = owner;
        stored.ClaimFence = fence;
        stored.ClaimExpiresAt = expiresAt;
        stored.Revision++;
        stored.LastObservedAt = DateTimeOffset.UtcNow;
        stored.LastModifiedAt = DateTimeOffset.UtcNow;

        try
        {
            await db.SaveChangesAsync();
            return string.Empty;
        }
        catch (DbUpdateException refused)
        {
            return refused.InnerException?.Message ?? refused.Message;
        }
    }

    private async Task RejectsExecutionAsync(World world, string constraintName, Action<WorkflowRunHarnessExecution> forge)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var execution = Execution(world, generation: 1);
        forge(execution);
        db.WorkflowRunHarnessExecution.Add(execution);

        var rejected = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();

        rejected.InnerException?.Message.ShouldContain(constraintName);
    }

    private async Task RejectsAttemptAsync(World world, WorkflowRunHarnessExecution execution, string constraintName, Action<WorkflowRunHarnessProcessAttempt> forge)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var attempt = Attempt(world, execution, ordinal: 1);
        forge(attempt);
        db.WorkflowRunHarnessProcessAttempt.Add(attempt);

        var rejected = await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();

        rejected.InnerException?.Message.ShouldContain(constraintName);
    }

    private async Task<World> SeedWorldAsync(Guid? workflowRunId = null)
    {
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var agentRunId = Guid.NewGuid();
        const long fenceEpoch = 7;
        var now = DateTimeOffset.UtcNow;

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.User.Add(new User { Id = actorId, Email = $"harness-exec-{actorId:N}@test.local", Name = $"harness-exec-{actorId:N}" });
        db.Team.Add(new Team { Id = teamId, Slug = $"harness-exec-{teamId:N}", Name = "Harness Execution Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = actorId, Role = TeamRole.Owner });
        await db.SaveChangesAsync();

        db.AgentRun.Add(new AgentRun
        {
            Id = agentRunId, TeamId = teamId, WorkflowRunId = workflowRunId, Harness = "codex-cli",
            Status = AgentRunStatus.Running, TaskJson = "{}", FenceEpoch = fenceEpoch,
            CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        });
        await db.SaveChangesAsync();
        return new World(teamId, actorId, agentRunId, fenceEpoch);
    }

    private async Task<WorkflowRunHarnessExecution> SeedExecutionAsync(World world, int generation, Guid? workflowRunId = null)
    {
        var execution = Execution(world, generation);
        execution.WorkflowRunId = workflowRunId;

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.WorkflowRunHarnessExecution.Add(execution);
        await db.SaveChangesAsync();
        return execution;
    }

    private async Task<WorkflowRunHarnessProcessAttempt> SeedAttemptAsync(World world, WorkflowRunHarnessExecution execution, int ordinal)
    {
        var attempt = Attempt(world, execution, ordinal);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.WorkflowRunHarnessProcessAttempt.Add(attempt);
        await db.SaveChangesAsync();
        return attempt;
    }

    private async Task AbandonAsync(World world, WorkflowRunHarnessExecution execution)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var terminalAt = DateTimeOffset.UtcNow;
        var stored = await db.WorkflowRunHarnessExecution.SingleAsync(candidate => candidate.TeamId == world.TeamId && candidate.Id == execution.Id);
        stored.State = HarnessExecutionState.Abandoned;
        stored.TerminalAt = terminalAt;
        stored.ErrorCode = "runner.host-gone";
        stored.ErrorMessage = "the runner host never came back";
        stored.LeaseOwnerId = null;
        stored.LeaseExpiresAt = null;
        stored.Revision++;
        stored.LastModifiedAt = terminalAt;
        await db.SaveChangesAsync();
    }

    private async Task LoseAsync(WorkflowRunHarnessProcessAttempt attempt)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var lostAt = DateTimeOffset.UtcNow;
        var stored = await db.WorkflowRunHarnessProcessAttempt.SingleAsync(candidate => candidate.Id == attempt.Id);
        stored.State = HarnessProcessAttemptState.Lost;
        stored.ExitedAt = lostAt;
        stored.LastObservedAt = lostAt;
        stored.ErrorCode = "runner.process-gone";
        stored.ErrorMessage = "the process vanished with no exit marker";
        stored.Revision++;
        stored.LastModifiedAt = lostAt;
        await db.SaveChangesAsync();
    }

    private static WorkflowRunHarnessExecution Execution(World world, int generation)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkflowRunHarnessExecution
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, AgentRunId = world.AgentRunId, Generation = generation,
            HarnessTypeKey = "codex-cli/v1", RunnerKind = "local", RunnerLocatorSchemaVersion = 1,
            RunnerHostAffinity = "worker-01", State = HarnessExecutionState.Pending, AttemptCount = 0,
            NextAttemptOrdinal = 1, LeaseFence = 0, Revision = 1, CreatedAt = now, LastModifiedAt = now,
        };
    }

    private static WorkflowRunHarnessProcessAttempt Attempt(World world, WorkflowRunHarnessExecution execution, int ordinal)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkflowRunHarnessProcessAttempt
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, AgentRunId = world.AgentRunId, ExecutionId = execution.Id,
            AttemptOrdinal = ordinal, WorkerFenceEpoch = world.FenceEpoch,
            RunnerLocatorJson = $"{{\"pid\": {1000 + ordinal}, \"spool\": \"/var/spool/{execution.Id:N}/{ordinal}\"}}",
            State = HarnessProcessAttemptState.Running, ClaimFence = 0, Revision = 1,
            StartedAt = now, LastObservedAt = now, CreatedAt = now, LastModifiedAt = now,
        };
    }

    private sealed record World(Guid TeamId, Guid ActorId, Guid AgentRunId, long FenceEpoch);
}
