using System.Collections.Concurrent;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.AgentRunLogging;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AgentRunLogCaptureRecoveryFlowTests
{
    private readonly PostgresFixture _fixture;

    public AgentRunLogCaptureRecoveryFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Exact_declaration_is_idempotent_but_conflicting_identity_and_direct_rewrite_fail_closed()
    {
        var world = await SeedWorldAsync();
        var service = Recovery(LogService());
        var sessionId = Guid.NewGuid();
        var request = Declaration(world, sessionId, 7, AgentRunLogKinds.StandardOutput);

        (await service.DeclareAsync(request, CancellationToken.None)).ShouldBe(new AgentRunLogCaptureDeclarationResult.Declared(1, 0));
        (await service.DeclareAsync(request, CancellationToken.None)).ShouldBe(new AgentRunLogCaptureDeclarationResult.Declared(0, 1));
        var conflict = request with { Streams = [new AgentRunLogExpectedStream(AgentRunLogKinds.StandardOutput, "application/json", "utf-8", "test-spool/v1")] };
        (await service.DeclareAsync(conflict, CancellationToken.None)).ShouldBe(new AgentRunLogCaptureDeclarationResult.Rejected(AgentRunLogCaptureDeclarationProblem.IdentityConflict));

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var row = await db.AgentRunLogCaptureIntent.SingleAsync(value => value.AgentRunId == world.AgentRunId);
        const string otherSource = "other-spool/v1";
        var rewritten = await Should.ThrowAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE agent_run_log_capture_intent SET capture_source = {otherSource}, revision = revision + 1, last_modified_at = clock_timestamp() WHERE id = {row.Id}"));
        rewritten.Message.ShouldContain("stable expectation identity is immutable");
        var deleted = await Should.ThrowAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM agent_run_log_capture_intent WHERE id = {row.Id}"));
        deleted.Message.ShouldContain("durable monotonic ledger");
    }

    [Fact]
    public async Task Terminal_grace_retry_without_a_terminal_observation_fails_closed_at_the_database_boundary()
    {
        var world = await SeedWorldAsync();
        var recovery = Recovery(LogService());
        var sessionId = Guid.NewGuid();
        await recovery.DeclareAsync(Declaration(world, sessionId, 7, AgentRunLogKinds.StandardOutput), CancellationToken.None);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var intent = await db.AgentRunLogCaptureIntent.SingleAsync(value => value.AgentRunId == world.AgentRunId);
        var owner = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run_log_capture_intent SET recovery_owner_id = {owner}, recovery_fence_epoch = 1, recovery_attempt_count = 1, recovery_started_at = clock_timestamp(), recovery_lease_expires_at = clock_timestamp() + interval '5 minutes', revision = revision + 1, last_modified_at = clock_timestamp() WHERE id = {intent.Id}");

        var malformed = await Should.ThrowAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE agent_run_log_capture_intent SET recovery_owner_id = NULL, recovery_lease_expires_at = NULL, last_error_code = 'terminal-grace-armed', last_error_message = 'missing observation', next_recovery_at = clock_timestamp() + interval '1 minute', revision = revision + 1, last_modified_at = clock_timestamp() WHERE id = {intent.Id}"));

        malformed.Message.ShouldContain("retry outcome requires a typed future retry");
        db.ChangeTracker.Clear();
        var preserved = await db.AgentRunLogCaptureIntent.AsNoTracking().SingleAsync(value => value.Id == intent.Id);
        preserved.RecoveryOwnerId.ShouldBe(owner, "the rejected malformed outcome must preserve the exact live claim");
        preserved.TerminalObservedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Terminal_recovery_distinguishes_a_finalized_zero_byte_stream_from_a_declared_stream_that_never_opened()
    {
        var world = await SeedWorldAsync();
        var logs = LogService();
        var recovery = Recovery(logs, new RecoveryTestOptions { TerminalGrace = TimeSpan.Zero });
        var sessionId = Guid.NewGuid();
        var declaration = Declaration(world, sessionId, 7, AgentRunLogKinds.StandardOutput, AgentRunLogKinds.StandardError);
        await recovery.DeclareAsync(declaration, CancellationToken.None);
        var stdout = (await logs.OpenAsync(Open(world, sessionId, AgentRunLogKinds.StandardOutput), CancellationToken.None)).ShouldBeOfType<AgentRunLogOpenResult.Opened>();
        await logs.FinalizeSourceAsync(new AgentRunLogFinalizeSourceRequest
        {
            TeamId = world.TeamId, AgentRunId = world.AgentRunId, StreamId = stdout.Metadata.StreamId,
            WorkerFenceEpoch = 7, CaptureSessionId = sessionId, ExpectedRevision = stdout.Metadata.Revision, ExpectedSourceOffsetBytes = 0,
        }, CancellationToken.None);
        await MarkTerminalAsync(world, AgentRunStatus.Succeeded, "{\"status\":\"Succeeded\"}");

        var summary = await recovery.ReconcileAsync(CancellationToken.None);
        await Task.Delay(10);
        var afterGrace = await recovery.ReconcileAsync(CancellationToken.None);

        summary.Claimed.ShouldBeGreaterThanOrEqualTo(2, "the system-wide bounded batch may also settle due intents left by another test");
        summary.Completed.ShouldBeGreaterThanOrEqualTo(1);
        afterGrace.CaptureFailed.ShouldBeGreaterThanOrEqualTo(1);
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var intents = await db.AgentRunLogCaptureIntent.Where(value => value.AgentRunId == world.AgentRunId).ToDictionaryAsync(value => value.StreamKind);
        intents[AgentRunLogKinds.StandardOutput].State.ShouldBe(AgentRunLogCaptureIntentState.Completed);
        intents[AgentRunLogKinds.StandardOutput].StreamId.ShouldBe(stdout.Metadata.StreamId);
        intents[AgentRunLogKinds.StandardError].State.ShouldBe(AgentRunLogCaptureIntentState.CaptureFailed);
        intents[AgentRunLogKinds.StandardError].StreamId.ShouldBeNull();
        intents[AgentRunLogKinds.StandardError].LastErrorCode.ShouldBe("expected-stream-missing");
        var run = await db.AgentRun.AsNoTracking().SingleAsync(value => value.Id == world.AgentRunId);
        run.Status.ShouldBe(AgentRunStatus.Succeeded);
        run.ResultJson.ShouldNotBeNull();
        run.ResultJson.ShouldContain("Succeeded");
    }

    [Fact]
    public async Task Backend_timeout_releases_the_claim_as_a_typed_retry_without_touching_AgentRun_terminal_state()
    {
        var world = await SeedWorldAsync();
        var realLogs = LogService();
        var sessionId = Guid.NewGuid();
        var setup = Recovery(realLogs);
        await setup.DeclareAsync(Declaration(world, sessionId, 7, AgentRunLogKinds.StandardOutput), CancellationToken.None);
        var opened = (await realLogs.OpenAsync(Open(world, sessionId, AgentRunLogKinds.StandardOutput), CancellationToken.None)).ShouldBeOfType<AgentRunLogOpenResult.Opened>();
        await realLogs.FinalizeSourceAsync(new AgentRunLogFinalizeSourceRequest
        {
            TeamId = world.TeamId, AgentRunId = world.AgentRunId, StreamId = opened.Metadata.StreamId,
            WorkerFenceEpoch = 7, CaptureSessionId = sessionId, ExpectedRevision = opened.Metadata.Revision, ExpectedSourceOffsetBytes = 0,
        }, CancellationToken.None);
        await MarkTerminalAsync(world, AgentRunStatus.Failed, "{\"status\":\"Failed\"}");
        var recovery = Recovery(new BlockingCompleteLogService(realLogs), new RecoveryTestOptions { OperationTimeout = TimeSpan.FromMilliseconds(75), TerminalGrace = TimeSpan.Zero });

        var intent = await ReconcileUntilAsync(recovery, world, value => value.LastErrorCode == "recovery-operation-timeout", "released back to Expected carrying a typed timeout error");

        intent.State.ShouldBe(AgentRunLogCaptureIntentState.Expected, "a timeout before a durable observation is conservatively retried from the claimed state");
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        intent.LastErrorCode.ShouldBe("recovery-operation-timeout");
        intent.RecoveryOwnerId.ShouldBeNull();
        intent.RecoveryLeaseExpiresAt.ShouldBeNull();
        (await db.AgentRun.AsNoTracking().SingleAsync(value => value.Id == world.AgentRunId)).Status.ShouldBe(AgentRunStatus.Failed);

        await Task.Delay(120);
        intent = await ReconcileUntilAsync(setup, world, value => value.State == AgentRunLogCaptureIntentState.Completed, "completed once the backend returned");
        intent.State.ShouldBe(AgentRunLogCaptureIntentState.Completed, "a later bounded pass must recover a finalized stream after the backend returns");
        (await db.AgentRun.AsNoTracking().SingleAsync(value => value.Id == world.AgentRunId)).Status.ShouldBe(AgentRunStatus.Failed);
    }

    [Fact]
    public async Task Terminal_open_stream_without_a_final_drain_receipt_becomes_typed_capture_failure_only()
    {
        var world = await SeedWorldAsync();
        var logs = LogService();
        var recovery = Recovery(logs, new RecoveryTestOptions { TerminalGrace = TimeSpan.Zero });
        var sessionId = Guid.NewGuid();
        await recovery.DeclareAsync(Declaration(world, sessionId, 7, AgentRunLogKinds.StandardOutput), CancellationToken.None);
        var opened = (await logs.OpenAsync(Open(world, sessionId, AgentRunLogKinds.StandardOutput), CancellationToken.None)).ShouldBeOfType<AgentRunLogOpenResult.Opened>();
        await MarkTerminalAsync(world, AgentRunStatus.Succeeded, "{\"status\":\"Succeeded\"}");

        await recovery.ReconcileAsync(CancellationToken.None);
        await Task.Delay(10);
        await recovery.ReconcileAsync(CancellationToken.None);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var intent = await db.AgentRunLogCaptureIntent.SingleAsync(value => value.AgentRunId == world.AgentRunId);
        intent.State.ShouldBe(AgentRunLogCaptureIntentState.CaptureFailed);
        intent.LastErrorCode.ShouldBe("source-not-finalized-after-terminal");
        var stream = await db.AgentRunLogStream.SingleAsync(value => value.Id == opened.Metadata.StreamId);
        stream.State.ShouldBe(AgentRunLogStreamState.CaptureFailed);
        stream.ErrorCode.ShouldBe("source-not-finalized-after-terminal");
        var run = await db.AgentRun.AsNoTracking().SingleAsync(value => value.Id == world.AgentRunId);
        run.Status.ShouldBe(AgentRunStatus.Succeeded);
        run.ResultJson.ShouldContain("Succeeded");
    }

    [Fact]
    public async Task Expired_claim_is_reclaimed_once_across_concurrent_workers_and_a_higher_run_fence_supersedes_old_intents()
    {
        var world = await SeedWorldAsync();
        var logs = LogService();
        var recovery = Recovery(logs, new RecoveryTestOptions { BaseDelay = TimeSpan.FromMilliseconds(100) });
        var oldSession = Guid.NewGuid();
        await recovery.DeclareAsync(Declaration(world, oldSession, 7, AgentRunLogKinds.StandardOutput), CancellationToken.None);
        Guid intentId;
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            intentId = (await db.AgentRunLogCaptureIntent.SingleAsync(value => value.AgentRunId == world.AgentRunId)).Id;
            var owner = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run_log_capture_intent SET recovery_owner_id = {owner}, recovery_fence_epoch = 1, recovery_attempt_count = 1, recovery_started_at = clock_timestamp(), recovery_lease_expires_at = clock_timestamp() + interval '100 milliseconds', revision = revision + 1, last_modified_at = clock_timestamp() WHERE id = {intentId}");
        }
        await Task.Delay(150);
        await RaiseFenceAsync(world, 8);

        var concurrent = await Task.WhenAll(recovery.ReconcileAsync(CancellationToken.None), recovery.ReconcileAsync(CancellationToken.None));
        concurrent.Sum(value => value.Claimed).ShouldBeGreaterThanOrEqualTo(1, "the system-wide workers may also claim unrelated due rows");
        using (var scope = _fixture.BeginScope())
        {
            var row = await scope.Resolve<CodeSpaceDbContext>().AgentRunLogCaptureIntent.SingleAsync(value => value.Id == intentId);
            row.RecoveryFenceEpoch.ShouldBe(2);
            row.RecoveryAttemptCount.ShouldBe(2);
            row.RecoveryOwnerId.ShouldBeNull();
            row.State.ShouldBe(AgentRunLogCaptureIntentState.Superseded);
        }

        var newSession = Guid.NewGuid();
        await recovery.DeclareAsync(Declaration(world, newSession, 8, AgentRunLogKinds.StandardOutput), CancellationToken.None);
        await Task.Delay(120);
        await recovery.ReconcileAsync(CancellationToken.None);
        using var finalScope = _fixture.BeginScope();
        var old = await finalScope.Resolve<CodeSpaceDbContext>().AgentRunLogCaptureIntent.SingleAsync(value => value.Id == intentId);
        old.State.ShouldBe(AgentRunLogCaptureIntentState.Superseded);
        old.LastErrorCode.ShouldBe("worker-fence-changed-before-settlement");
        var active = await finalScope.Resolve<CodeSpaceDbContext>().AgentRunLogCaptureIntent.SingleAsync(value => value.CaptureSessionId == newSession);
        active.State.ShouldBe(AgentRunLogCaptureIntentState.Expected);
        active.RecoveryAttemptCount.ShouldBe(0, "a healthy Running intent is not rewritten by the recovery sweep");
    }

    [Fact]
    public async Task A_bounded_worker_claims_only_the_wave_it_can_start_before_its_lease_budget()
    {
        var neighbour = await SeedDueFinalizedStreamNeighbourAsync();
        var first = await SeedWorldAsync();
        var second = await SeedWorldAsync();
        var logs = LogService();
        var firstSession = Guid.NewGuid();
        var secondSession = Guid.NewGuid();
        var gated = new GateFirstCompleteLogService(logs, first.AgentRunId, second.AgentRunId);
        var recovery = Recovery(gated, new RecoveryTestOptions { MaxConcurrency = 1 });
        await recovery.DeclareAsync(Declaration(first, firstSession, 7, AgentRunLogKinds.StandardOutput), CancellationToken.None);
        await recovery.DeclareAsync(Declaration(second, secondSession, 7, AgentRunLogKinds.StandardOutput), CancellationToken.None);
        await SeedFinalizedTerminalStreamAsync(first, logs, firstSession);
        await SeedFinalizedTerminalStreamAsync(second, logs, secondSession);

        var reconcile = await ReconcileUntilPausedAsync(recovery, gated.Entered, first);
        using (var scope = _fixture.BeginScope())
        {
            var rows = await scope.Resolve<CodeSpaceDbContext>().AgentRunLogCaptureIntent
                .Where(value => value.AgentRunId == first.AgentRunId || value.AgentRunId == second.AgentRunId).ToListAsync();
            rows.Count(value => value.RecoveryOwnerId != null).ShouldBe(1, "later work must remain unclaimed until a worker can start it inside a fresh lease");
            rows.Count(value => value.RecoveryOwnerId == null).ShouldBe(1);
        }

        var intents = await ReleaseAndReconcileUntilCompletedAsync(recovery, reconcile, gated.Release, first, second);

        intents.ShouldAllBe(value => value.RecoveryAttemptCount == 1, "each owned intent is claimed once, by a wave that could start it inside its lease");
        (await IntentAsync(neighbour)).State.ShouldBe(AgentRunLogCaptureIntentState.Completed,
            "the neighbour's CompleteAsync ran first through the same gated waves, so the gate was exercised against a stranger and stayed shut");
    }

    [Fact]
    public async Task A_reclaimed_recovery_worker_cannot_commit_stream_health_after_its_lease_expires()
    {
        var world = await SeedWorldAsync();
        var logs = LogService();
        var recovery = Recovery(logs);
        var sessionId = Guid.NewGuid();
        await recovery.DeclareAsync(Declaration(world, sessionId, 7, AgentRunLogKinds.StandardOutput), CancellationToken.None);
        var opened = (await logs.OpenAsync(Open(world, sessionId, AgentRunLogKinds.StandardOutput), CancellationToken.None)).ShouldBeOfType<AgentRunLogOpenResult.Opened>();
        var finalized = (await logs.FinalizeSourceAsync(new AgentRunLogFinalizeSourceRequest
        {
            TeamId = world.TeamId, AgentRunId = world.AgentRunId, StreamId = opened.Metadata.StreamId,
            WorkerFenceEpoch = 7, CaptureSessionId = sessionId, ExpectedRevision = opened.Metadata.Revision, ExpectedSourceOffsetBytes = 0,
        }, CancellationToken.None)).ShouldBeOfType<AgentRunLogFinalizeSourceResult.Finalized>();
        await MarkTerminalAsync(world, AgentRunStatus.Succeeded, "{\"status\":\"Succeeded\"}");

        Guid intentId;
        var staleOwner = Guid.NewGuid();
        var currentOwner = Guid.NewGuid();
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            intentId = (await db.AgentRunLogCaptureIntent.SingleAsync(value => value.AgentRunId == world.AgentRunId)).Id;
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run_log_capture_intent SET recovery_owner_id = {staleOwner}, verification_claim_marker = verification_claim_marker + 1, recovery_fence_epoch = 1, recovery_attempt_count = 1, recovery_started_at = clock_timestamp(), recovery_lease_expires_at = clock_timestamp() + interval '50 milliseconds', revision = revision + 1, last_modified_at = clock_timestamp() WHERE id = {intentId}");
        }
        await Task.Delay(150);
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run_log_capture_intent SET recovery_owner_id = {currentOwner}, verification_claim_marker = verification_claim_marker + 1, recovery_fence_epoch = 2, recovery_attempt_count = 2, recovery_lease_expires_at = clock_timestamp() + interval '5 seconds', revision = revision + 1, last_modified_at = clock_timestamp() WHERE id = {intentId}");
        }

        var stale = await logs.FailCaptureAsync(new AgentRunLogFailCaptureRequest
        {
            TeamId = world.TeamId, AgentRunId = world.AgentRunId, StreamId = opened.Metadata.StreamId,
            WorkerFenceEpoch = 7, CaptureSessionId = sessionId, ExpectedRevision = finalized.Metadata.Revision,
            ErrorCode = "stale-worker-must-not-win", RecoveryClaim = new AgentRunLogRecoveryClaimRef(intentId, staleOwner, 1),
        }, CancellationToken.None);

        stale.ShouldBeOfType<AgentRunLogFailCaptureResult.Rejected>().Problem.Code.ShouldBe(AgentRunLogProblemCode.StaleRecoveryClaim);
        using (var scope = _fixture.BeginScope())
        {
            var stream = await scope.Resolve<CodeSpaceDbContext>().AgentRunLogStream.AsNoTracking().SingleAsync(value => value.Id == opened.Metadata.StreamId);
            stream.State.ShouldBe(AgentRunLogStreamState.Open);
            stream.ErrorCode.ShouldBeNull();
        }

        var current = await logs.CompleteAsync(new AgentRunLogCompleteRequest
        {
            TeamId = world.TeamId, AgentRunId = world.AgentRunId, StreamId = opened.Metadata.StreamId,
            WorkerFenceEpoch = 7, CaptureSessionId = sessionId, ExpectedRevision = finalized.Metadata.Revision,
            OperationTimeout = TimeSpan.FromSeconds(1), RecoveryClaim = new AgentRunLogRecoveryClaimRef(intentId, currentOwner, 2),
        }, CancellationToken.None);

        current.ShouldBeOfType<AgentRunLogCompleteResult.Completed>();
        using var finalScope = _fixture.BeginScope();
        var dbFinal = finalScope.Resolve<CodeSpaceDbContext>();
        (await dbFinal.AgentRunLogStream.AsNoTracking().SingleAsync(value => value.Id == opened.Metadata.StreamId)).State.ShouldBe(AgentRunLogStreamState.Completed);
        var run = await dbFinal.AgentRun.AsNoTracking().SingleAsync(value => value.Id == world.AgentRunId);
        run.Status.ShouldBe(AgentRunStatus.Succeeded);
        run.ResultJson.ShouldContain("Succeeded");
    }

    [Fact]
    public async Task A_finalized_session_superseded_by_a_reattach_cannot_borrow_the_later_sessions_completed_stream()
    {
        var world = await SeedWorldAsync();
        var logs = LogService();
        var recovery = Recovery(logs, new RecoveryTestOptions { TerminalGrace = TimeSpan.Zero });
        var oldSession = Guid.NewGuid();
        await recovery.DeclareAsync(Declaration(world, oldSession, 7, AgentRunLogKinds.StandardOutput), CancellationToken.None);
        var oldOpen = (await logs.OpenAsync(Open(world, oldSession, AgentRunLogKinds.StandardOutput), CancellationToken.None)).ShouldBeOfType<AgentRunLogOpenResult.Opened>();
        await logs.FinalizeSourceAsync(new AgentRunLogFinalizeSourceRequest
        {
            TeamId = world.TeamId, AgentRunId = world.AgentRunId, StreamId = oldOpen.Metadata.StreamId,
            WorkerFenceEpoch = 7, CaptureSessionId = oldSession, ExpectedRevision = oldOpen.Metadata.Revision, ExpectedSourceOffsetBytes = 0,
        }, CancellationToken.None);

        var newSession = Guid.NewGuid();
        await recovery.DeclareAsync(Declaration(world, newSession, 7, AgentRunLogKinds.StandardOutput), CancellationToken.None);
        var newOpen = (await logs.OpenAsync(Open(world, newSession, AgentRunLogKinds.StandardOutput), CancellationToken.None)).ShouldBeOfType<AgentRunLogOpenResult.Opened>();
        var finalized = (await logs.FinalizeSourceAsync(new AgentRunLogFinalizeSourceRequest
        {
            TeamId = world.TeamId, AgentRunId = world.AgentRunId, StreamId = newOpen.Metadata.StreamId,
            WorkerFenceEpoch = 7, CaptureSessionId = newSession, ExpectedRevision = newOpen.Metadata.Revision, ExpectedSourceOffsetBytes = 0,
        }, CancellationToken.None)).ShouldBeOfType<AgentRunLogFinalizeSourceResult.Finalized>();
        (await logs.CompleteAsync(new AgentRunLogCompleteRequest
        {
            TeamId = world.TeamId, AgentRunId = world.AgentRunId, StreamId = newOpen.Metadata.StreamId,
            WorkerFenceEpoch = 7, CaptureSessionId = newSession, ExpectedRevision = finalized.Metadata.Revision,
            OperationTimeout = TimeSpan.FromSeconds(1),
        }, CancellationToken.None)).ShouldBeOfType<AgentRunLogCompleteResult.Completed>();
        await MarkTerminalAsync(world, AgentRunStatus.Succeeded, "{\"status\":\"Succeeded\"}");

        await recovery.ReconcileAsync(CancellationToken.None);

        using var scope = _fixture.BeginScope();
        var intents = await scope.Resolve<CodeSpaceDbContext>().AgentRunLogCaptureIntent.Where(value => value.AgentRunId == world.AgentRunId)
            .ToDictionaryAsync(value => value.CaptureSessionId);
        intents[oldSession].State.ShouldBe(AgentRunLogCaptureIntentState.Superseded);
        intents[oldSession].LastErrorCode.ShouldBe("source-finalized-before-superseded");
        intents[newSession].State.ShouldBe(AgentRunLogCaptureIntentState.Completed);
    }

    [Fact]
    public async Task A_completed_stream_from_an_earlier_worker_fence_cannot_satisfy_a_later_exact_intent_even_with_the_same_session()
    {
        var world = await SeedWorldAsync();
        var logs = LogService();
        var recovery = Recovery(logs, new RecoveryTestOptions { TerminalGrace = TimeSpan.Zero });
        var sessionId = Guid.NewGuid();
        await recovery.DeclareAsync(Declaration(world, sessionId, 7, AgentRunLogKinds.StandardOutput), CancellationToken.None);
        var opened = (await logs.OpenAsync(Open(world, sessionId, AgentRunLogKinds.StandardOutput), CancellationToken.None)).ShouldBeOfType<AgentRunLogOpenResult.Opened>();
        var finalized = (await logs.FinalizeSourceAsync(new AgentRunLogFinalizeSourceRequest
        {
            TeamId = world.TeamId, AgentRunId = world.AgentRunId, StreamId = opened.Metadata.StreamId,
            WorkerFenceEpoch = 7, CaptureSessionId = sessionId, ExpectedRevision = opened.Metadata.Revision, ExpectedSourceOffsetBytes = 0,
        }, CancellationToken.None)).ShouldBeOfType<AgentRunLogFinalizeSourceResult.Finalized>();
        (await logs.CompleteAsync(new AgentRunLogCompleteRequest
        {
            TeamId = world.TeamId, AgentRunId = world.AgentRunId, StreamId = opened.Metadata.StreamId,
            WorkerFenceEpoch = 7, CaptureSessionId = sessionId, ExpectedRevision = finalized.Metadata.Revision,
            OperationTimeout = TimeSpan.FromSeconds(1),
        }, CancellationToken.None)).ShouldBeOfType<AgentRunLogCompleteResult.Completed>();

        await RaiseFenceAsync(world, 8);
        (await recovery.DeclareAsync(Declaration(world, sessionId, 8, AgentRunLogKinds.StandardOutput), CancellationToken.None))
            .ShouldBe(new AgentRunLogCaptureDeclarationResult.Declared(1, 0));
        (await logs.OpenAsync(Open(world, sessionId, AgentRunLogKinds.StandardOutput, 8), CancellationToken.None))
            .ShouldBeOfType<AgentRunLogOpenResult.Rejected>().Problem.Code.ShouldBe(AgentRunLogProblemCode.StreamTerminal);
        await MarkTerminalAsync(world, AgentRunStatus.Succeeded, "{\"status\":\"Succeeded\"}");

        await recovery.ReconcileAsync(CancellationToken.None);

        using var scope = _fixture.BeginScope();
        var intents = await scope.Resolve<CodeSpaceDbContext>().AgentRunLogCaptureIntent.Where(value => value.AgentRunId == world.AgentRunId)
            .ToDictionaryAsync(value => value.WorkerFenceEpoch);
        intents[7].State.ShouldBe(AgentRunLogCaptureIntentState.Superseded);
        intents[8].State.ShouldBe(AgentRunLogCaptureIntentState.Superseded, "a completed historical stream is not evidence for a later exact worker identity");
        intents[8].LastErrorCode.ShouldBe("stream-claim-identity-mismatch");
        intents[8].StreamId.ShouldBeNull("a mismatched stream must never be admitted into the later intent");
    }

    [Fact]
    public async Task A_worker_fence_bump_after_the_stream_effect_but_before_settlement_atomically_supersedes_the_intent()
    {
        var neighbour = await SeedDueOpenStreamNeighbourAsync();
        var world = await SeedWorldAsync();
        var logs = LogService();
        var gated = new GateAfterFailLogService(logs, world.AgentRunId);
        var recovery = Recovery(gated, new RecoveryTestOptions { TerminalGrace = TimeSpan.Zero });
        var sessionId = Guid.NewGuid();
        await recovery.DeclareAsync(Declaration(world, sessionId, 7, AgentRunLogKinds.StandardOutput), CancellationToken.None);
        var opened = (await logs.OpenAsync(Open(world, sessionId, AgentRunLogKinds.StandardOutput), CancellationToken.None)).ShouldBeOfType<AgentRunLogOpenResult.Opened>();
        await MarkTerminalAsync(world, AgentRunStatus.Succeeded, "{\"status\":\"Succeeded\"}");

        await ReconcileUntilAsync(recovery, world, value => value.TerminalObservedAt != null, "terminal grace armed by a first terminal observation");
        var reconcile = await ReconcileUntilPausedAsync(recovery, gated.EffectCommitted, world);
        await RaiseFenceAsync(world, 8);
        gated.Release();

        var summary = await reconcile;

        // >= not == : Superseded is a system-wide bounded-batch tally (same shape as the other reconcile summaries
        // in this file, e.g. Claimed above) — another test's due intent can settle in the same wave. The intent's
        // own State below (Superseded, not CaptureFailed) is what proves THIS row was superseded, not merely tallied.
        summary.Superseded.ShouldBeGreaterThanOrEqualTo(1);
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var intent = await db.AgentRunLogCaptureIntent.SingleAsync(value => value.AgentRunId == world.AgentRunId);
        intent.State.ShouldBe(AgentRunLogCaptureIntentState.Superseded,
            "the observation made under fence 7 cannot settle terminal intent state after fence 8 owns the run");
        intent.LastErrorCode.ShouldBe("worker-fence-changed-before-settlement");
        (await db.AgentRunLogStream.SingleAsync(value => value.Id == opened.Metadata.StreamId)).State.ShouldBe(AgentRunLogStreamState.CaptureFailed,
            "the fence bump landed after this stream's committed effect; Open would mean it landed before the effect, which is a different interleaving");
        var run = await db.AgentRun.AsNoTracking().SingleAsync(value => value.Id == world.AgentRunId);
        run.FenceEpoch.ShouldBe(8);
        run.Status.ShouldBe(AgentRunStatus.Succeeded);
        (await db.AgentRunLogStream.SingleAsync(value => value.AgentRunId == neighbour.AgentRunId)).State.ShouldBe(AgentRunLogStreamState.CaptureFailed,
            "the neighbour's effect ran through the same gated waves, so the gate was exercised against a stranger and stayed shut");
    }

    [Fact]
    public async Task Many_healthy_active_intents_are_not_rewritten_and_cannot_starve_a_terminal_gap()
    {
        var recovery = Recovery(LogService(), new RecoveryTestOptions { MaxConcurrency = 4, TerminalGrace = TimeSpan.FromMilliseconds(50) });
        var active = new List<World>();
        for (var index = 0; index < 24; index++)
        {
            var world = await SeedWorldAsync();
            active.Add(world);
            await recovery.DeclareAsync(Declaration(world, Guid.NewGuid(), 7, AgentRunLogKinds.StandardOutput), CancellationToken.None);
        }
        var terminal = await SeedWorldAsync();
        await recovery.DeclareAsync(Declaration(terminal, Guid.NewGuid(), 7, AgentRunLogKinds.StandardOutput), CancellationToken.None);
        await MarkTerminalAsync(terminal, AgentRunStatus.Succeeded, "{\"status\":\"Succeeded\"}");

        await recovery.ReconcileAsync(CancellationToken.None);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var terminalIntent = await db.AgentRunLogCaptureIntent.SingleAsync(value => value.AgentRunId == terminal.AgentRunId);
        terminalIntent.TerminalObservedAt.ShouldNotBeNull("the terminal tenant must enter recovery even behind more than one full batch of active tenants");
        var activeIds = active.Select(value => value.AgentRunId).ToArray();
        var activeIntents = await db.AgentRunLogCaptureIntent.Where(value => activeIds.Contains(value.AgentRunId)).ToListAsync();
        activeIntents.ShouldAllBe(value => value.RecoveryAttemptCount == 0 && value.Revision == 1 && value.State == AgentRunLogCaptureIntentState.Expected);
    }

    [Fact]
    public async Task A_later_tenant_head_does_not_make_the_fair_wave_skip_more_due_work_from_an_earlier_tenant()
    {
        var early = await SeedWorldAsync();
        var later = await SeedWorldAsync();
        var recovery = Recovery(LogService(), new RecoveryTestOptions { MaxConcurrency = 2, TerminalGrace = TimeSpan.FromSeconds(1) });
        var earlyKinds = new[] { "test/alpha/v1", "test/beta/v1", "test/gamma/v1" };
        await recovery.DeclareAsync(Declaration(early, Guid.NewGuid(), 7, earlyKinds), CancellationToken.None);
        await Task.Delay(20);
        await recovery.DeclareAsync(Declaration(later, Guid.NewGuid(), 7, "test/later/v1"), CancellationToken.None);
        await MarkTerminalAsync(early, AgentRunStatus.Succeeded, "{}");
        await MarkTerminalAsync(later, AgentRunStatus.Succeeded, "{}");

        await recovery.ReconcileAsync(CancellationToken.None);

        using var scope = _fixture.BeginScope();
        var ids = new[] { early.AgentRunId, later.AgentRunId };
        var intents = await scope.Resolve<CodeSpaceDbContext>().AgentRunLogCaptureIntent.Where(value => ids.Contains(value.AgentRunId)).ToListAsync();
        intents.Count.ShouldBe(4);
        intents.ShouldAllBe(value => value.RecoveryAttemptCount == 1 && value.TerminalObservedAt != null && value.LastErrorCode == "terminal-grace-armed",
            "each fair wave must revisit the queue head after settlement instead of skipping earlier work behind a later tenant cursor");
    }

    [Fact]
    public async Task Locked_earliest_fair_heads_are_skipped_and_the_bounded_wave_backfills_later_tenants()
    {
        var worlds = new List<World>();
        var recovery = Recovery(LogService(), new RecoveryTestOptions { MaxConcurrency = 2 });
        for (var index = 0; index < 4; index++)
        {
            var world = await SeedWorldAsync();
            worlds.Add(world);
            await recovery.DeclareAsync(Declaration(world, Guid.NewGuid(), 7, AgentRunLogKinds.StandardOutput), CancellationToken.None);
            await MarkTerminalAsync(world, AgentRunStatus.Succeeded, "{}");
        }

        using var lockScope = _fixture.BeginScope();
        var lockDb = lockScope.Resolve<CodeSpaceDbContext>();
        await using var lockTransaction = await lockDb.Database.BeginTransactionAsync();
        var locked = await lockDb.AgentRunLogCaptureIntent.FromSqlRaw("""
            WITH fair AS MATERIALIZED (
                SELECT DISTINCT ON (intent.team_id) intent.id, intent.team_id, intent.next_recovery_at
                FROM agent_run_log_capture_intent intent
                JOIN agent_run run ON run.team_id = intent.team_id AND run.id = intent.agent_run_id
                WHERE intent.state IN ('Expected', 'Opened', 'SourceFinalized')
                  AND intent.next_recovery_at <= clock_timestamp()
                  AND (intent.recovery_lease_expires_at IS NULL OR intent.recovery_lease_expires_at <= clock_timestamp())
                  AND (run.status <> 'Running' OR run.fence_epoch <> intent.worker_fence_epoch)
                ORDER BY intent.team_id, intent.next_recovery_at, intent.id
            ), picked AS MATERIALIZED (
                SELECT * FROM fair ORDER BY next_recovery_at, team_id, id LIMIT 2
            )
            SELECT intent.*, intent.xmin FROM agent_run_log_capture_intent intent
            JOIN picked ON picked.id = intent.id
            ORDER BY intent.next_recovery_at, intent.team_id, intent.id
            FOR UPDATE OF intent
            """).ToListAsync();
        locked.Count.ShouldBe(2);
        var lockedIds = locked.Select(value => value.Id).ToHashSet();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var summary = await recovery.ReconcileAsync(timeout.Token);

        summary.Claimed.ShouldBeGreaterThanOrEqualTo(2, "locked queue heads must not consume either slot in the bounded wave or hide later unlocked tenants");
        using var verifyScope = _fixture.BeginScope();
        var runIds = worlds.Select(value => value.AgentRunId).ToArray();
        var intents = await verifyScope.Resolve<CodeSpaceDbContext>().AgentRunLogCaptureIntent.AsNoTracking().Where(value => runIds.Contains(value.AgentRunId)).ToListAsync();
        intents.Count(value => !lockedIds.Contains(value.Id) && value.RecoveryAttemptCount == 1).ShouldBeGreaterThanOrEqualTo(2,
            "both bounded slots must be backfilled from later unlocked tenants");
        await lockTransaction.RollbackAsync();
    }

    [Fact]
    public async Task Repeated_transient_provider_failure_exits_the_hot_index_as_external_state_indeterminate()
    {
        var world = await SeedWorldAsync();
        var logs = LogService();
        var sessionId = Guid.NewGuid();
        var setup = Recovery(logs);
        await setup.DeclareAsync(Declaration(world, sessionId, 7, AgentRunLogKinds.StandardOutput), CancellationToken.None);
        var opened = (await logs.OpenAsync(Open(world, sessionId, AgentRunLogKinds.StandardOutput), CancellationToken.None)).ShouldBeOfType<AgentRunLogOpenResult.Opened>();
        await logs.FinalizeSourceAsync(new AgentRunLogFinalizeSourceRequest
        {
            TeamId = world.TeamId, AgentRunId = world.AgentRunId, StreamId = opened.Metadata.StreamId,
            WorkerFenceEpoch = 7, CaptureSessionId = sessionId, ExpectedRevision = opened.Metadata.Revision, ExpectedSourceOffsetBytes = 0,
        }, CancellationToken.None);
        await MarkTerminalAsync(world, AgentRunStatus.Succeeded, "{\"status\":\"Succeeded\"}");
        var recovery = Recovery(new AlwaysRetryableCompleteLogService(logs), new RecoveryTestOptions
        {
            BaseDelay = TimeSpan.FromMilliseconds(10), MaxDelay = TimeSpan.FromMilliseconds(20), MaxAttempts = 2,
        });

        var intent = await ReconcileUntilAsync(recovery, world, value => value.State == AgentRunLogCaptureIntentState.ExternalStateIndeterminate, "exhausted into ExternalStateIndeterminate");

        intent.State.ShouldBe(AgentRunLogCaptureIntentState.ExternalStateIndeterminate);
        intent.LastErrorCode.ShouldBe("recovery-exhausted");
        intent.RecoveryAttemptCount.ShouldBe(2);
        var terminalRevision = intent.Revision;
        await Task.Delay(30);
        await recovery.ReconcileAsync(CancellationToken.None);
        using var finalScope = _fixture.BeginScope();
        var unchanged = await finalScope.Resolve<CodeSpaceDbContext>().AgentRunLogCaptureIntent.AsNoTracking().SingleAsync(value => value.AgentRunId == world.AgentRunId);
        unchanged.State.ShouldBe(AgentRunLogCaptureIntentState.ExternalStateIndeterminate);
        unchanged.Revision.ShouldBe(terminalRevision, "the exhausted target must remain outside the hot recovery index even when a concurrent test leaves another due intent");
        unchanged.RecoveryAttemptCount.ShouldBe(2);
    }

    [Fact]
    public async Task A_settlement_slower_than_the_retry_it_schedules_still_releases_its_claim()
    {
        // A settlement reads its DB clock, schedules the retry from it, then reads verification progress before it
        // writes. Holding THIS intent's settlement between the two for longer than the retry it schedules leaves the
        // retry instant in the past when the write lands, which a cold or loaded worker does with nothing held. Refused,
        // the intent stays leased and idle until the lease expires, and the re-claim spends another attempt.
        var world = await SeedWorldAsync();
        var logs = LogService();
        var sessionId = Guid.NewGuid();
        await Recovery(logs).DeclareAsync(Declaration(world, sessionId, 7, AgentRunLogKinds.StandardOutput), CancellationToken.None);
        await SeedFinalizedTerminalStreamAsync(world, logs, sessionId);

        var retryDelay = TimeSpan.FromMilliseconds(100);
        var slowSettlement = new SlowCaptureSettlementInterceptor(world.AgentRunId, retryDelay * 3);
        var recovery = Recovery(new AlwaysRetryableCompleteLogService(logs), new RecoveryTestOptions { BaseDelay = retryDelay, MaxDelay = retryDelay }, slowSettlement);

        var intent = await ReconcileUntilAsync(recovery, world, value => value.RecoveryAttemptCount > 0, "claimed by a recovery wave");

        slowSettlement.Held.ShouldBeTrue("the seam never held this intent's settlement, so its write was never slower than its retry");
        intent.RecoveryOwnerId.ShouldBeNull("a settlement slower than the retry it scheduled must still commit and release its claim");
        intent.State.ShouldBe(AgentRunLogCaptureIntentState.SourceFinalized);
        intent.LastErrorCode.ShouldBe("complete-backend-unavailable");
        intent.RecoveryAttemptCount.ShouldBe(1);
        intent.VerificationStalledAttempts.ShouldBe(1);
    }

    [Theory]
    [InlineData("transaction_timestamp()", false)]                              // due the instant its settlement began: nothing was scheduled
    [InlineData("transaction_timestamp() + interval '50 milliseconds'", true)] // due after its settlement began, though the write lands later
    public async Task A_retry_must_be_due_after_the_settlement_that_scheduled_it_began_not_after_its_write(string nextRecoveryAt, bool admitted)
    {
        var world = await SeedWorldAsync();
        await Recovery(LogService()).DeclareAsync(Declaration(world, Guid.NewGuid(), 7, AgentRunLogKinds.StandardOutput), CancellationToken.None);
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var intent = await db.AgentRunLogCaptureIntent.SingleAsync(value => value.AgentRunId == world.AgentRunId);
        var owner = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run_log_capture_intent SET recovery_owner_id = {owner}, recovery_fence_epoch = 1, recovery_attempt_count = 1, recovery_started_at = clock_timestamp(), recovery_lease_expires_at = clock_timestamp() + interval '5 minutes', revision = revision + 1, last_modified_at = clock_timestamp() WHERE id = {intent.Id}");

        var refusal = await Record.ExceptionAsync(() => SettleRetryAfterPauseAsync(db, intent.Id, nextRecoveryAt));

        (refusal == null).ShouldBe(admitted, refusal?.Message);
        (refusal?.Message.Contains("retry outcome requires a typed future retry") ?? false).ShouldBe(!admitted, refusal?.Message);
        db.ChangeTracker.Clear();
        (await db.AgentRunLogCaptureIntent.AsNoTracking().SingleAsync(value => value.Id == intent.Id)).RecoveryOwnerId.ShouldBe(admitted ? null : owner);
    }

    /// <summary>
    /// Settles a claimed intent as a typed retry due at <paramref name="nextRecoveryAt"/>, a SQL expression, after a pause
    /// longer than that retry's delay. The pause stands in for the reads a real settlement makes between the clock read it
    /// schedules from and the write the guard inspects.
    /// </summary>
    private static async Task SettleRetryAfterPauseAsync(CodeSpaceDbContext db, Guid intentId, string nextRecoveryAt)
    {
        var settle = "UPDATE agent_run_log_capture_intent SET recovery_owner_id = NULL, recovery_lease_expires_at = NULL, last_error_code = 'complete-backend-unavailable', "
            + "last_error_message = 'The finalized stream could not yet be verified.', next_recovery_at = " + nextRecoveryAt + ", revision = revision + 1, last_modified_at = clock_timestamp() WHERE id = {0}";
        await using var transaction = await db.Database.BeginTransactionAsync();

        await db.Database.ExecuteSqlRawAsync("SELECT pg_sleep(0.2)");
        await db.Database.ExecuteSqlRawAsync(settle, intentId);

        await transaction.CommitAsync();
    }

    [Theory]
    [InlineData(8, AgentRunLogCaptureIntentState.SourceFinalized, "complete-backend-unavailable")] // the observed retry is the write refused
    [InlineData(1, AgentRunLogCaptureIntentState.ExternalStateIndeterminate, "recovery-exhausted")] // the settlement exhausts the observed retry, and that write is refused
    public async Task A_settlement_the_database_refuses_is_logged_with_the_write_it_refused_and_still_waits_out_its_lease(int maxAttempts, AgentRunLogCaptureIntentState refusedState, string refusedCode)
    {
        // A settlement that raises leaves its claim leased and idle until the lease expires, and is counted as a lost
        // lease. When the cause is a guard refusal, the code and the database contract disagree, and without the cause
        // in the log that reads as an unreproducible flake. The guard's clauses branch on the state and code the write
        // carries, and a settlement can replace the outcome it observed before it writes, so the log must name the write.
        var scene = await SeedOwnedIntentBehindDueNeighbourAsync();
        var refusal = new RefusedCaptureSettlementInterceptor(scene.IntentId);
        var log = new RecordedRecoveryLog();
        var recovery = Recovery(new AlwaysRetryableCompleteLogService(scene.Logs), new RecoveryTestOptions { MaxAttempts = maxAttempts }, refusal, log);

        var summary = await ReconcileUntilFaultedAsync(recovery, () => refusal.Refused, scene.Owned);

        var entry = await ShouldWaitOutItsLeaseAloneAsync(scene, summary, log);
        entry.Properties["Outcome"].ShouldBe(refusedState);
        entry.Properties["OutcomeCode"].ShouldBe(refusedCode);
        entry.Properties["SqlState"].ShouldBe(PostgresErrorCodes.RaiseException);
        entry.Properties["MessageText"].ShouldBeOfType<string>().ShouldContain($"(id={scene.IntentId})");
        entry.Exception.ShouldBeOfType<DbUpdateException>().InnerException.ShouldBeOfType<PostgresException>();
    }

    [Fact]
    public async Task A_settlement_that_outlives_its_budget_is_logged_without_a_database_cause_and_still_waits_out_its_lease()
    {
        // The likeliest cause in production is not a Postgres error at all: the settlement outlives its budget waiting on
        // a row lock or a slow read, and is cancelled. The exception is then the whole cause, SqlState and MessageText are
        // null, and naming them must not throw from inside the catch, where it would fault the wave and stop its claims.
        var scene = await SeedOwnedIntentBehindDueNeighbourAsync();
        var held = new SlowCaptureSettlementInterceptor(scene.Owned.AgentRunId, Timeout.InfiniteTimeSpan);
        var log = new RecordedRecoveryLog();
        var recovery = Recovery(new AlwaysRetryableCompleteLogService(scene.Logs), interceptor: held, logger: log);

        var summary = await ReconcileUntilFaultedAsync(recovery, () => held.Held, scene.Owned);

        var entry = await ShouldWaitOutItsLeaseAloneAsync(scene, summary, log);
        entry.Properties["Outcome"].ShouldBe(AgentRunLogCaptureIntentState.SourceFinalized);
        entry.Properties["OutcomeCode"].ShouldBe("complete-backend-unavailable");
        entry.Properties["SqlState"].ShouldBeNull();
        entry.Properties["MessageText"].ShouldBeNull();
        entry.Exception.ShouldBeAssignableTo<OperationCanceledException>();
    }

    /// <summary>
    /// Seeds this test's terminal run with a finalized stream, so the next wave settles its intent, behind another
    /// tenant's due neighbour that the same waves settle first through the same seam.
    /// </summary>
    private async Task<SettlementScene> SeedOwnedIntentBehindDueNeighbourAsync()
    {
        var neighbour = await SeedDueFinalizedStreamNeighbourAsync();
        var owned = await SeedWorldAsync();
        var logs = LogService();
        var sessionId = Guid.NewGuid();
        await Recovery(logs).DeclareAsync(Declaration(owned, sessionId, 7, AgentRunLogKinds.StandardOutput), CancellationToken.None);
        await SeedFinalizedTerminalStreamAsync(owned, logs, sessionId);

        return new SettlementScene(neighbour, owned, (await IntentAsync(owned)).Id, logs);
    }

    /// <summary>
    /// Asserts that the faulted settlement kept today's outcome — counted as a lost lease, its claim left leased and
    /// unwritten — and that the seam touched only this test's intent, then returns the one entry that names it.
    /// </summary>
    private async Task<RecordedEntry> ShouldWaitOutItsLeaseAloneAsync(SettlementScene scene, AgentRunLogCaptureRecoverySummary summary, RecordedRecoveryLog log)
    {
        // This is the wave that reached the faulted intent, so it cannot have been crowded out; a stranger's lost lease
        // could only satisfy the tally falsely, never fail it.
        summary.LostLease.ShouldBeGreaterThanOrEqualTo(1, "a settlement that raises is still counted as a lost lease");

        await ShouldBeLeftLeasedAndUnwrittenAsync(scene.Owned);
        await ShouldHaveLeftTheNeighbourAloneAsync(scene, log);

        var entry = log.About(scene.IntentId).ShouldHaveSingleItem("the faulted settlement must be logged once, naming its intent");
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Properties["RunId"].ShouldBe(scene.Owned.AgentRunId);
        entry.Properties["{OriginalFormat}"].ShouldBeOfType<string>().ShouldContain("until its recovery lease expires");

        return entry;
    }

    private async Task ShouldBeLeftLeasedAndUnwrittenAsync(World owned)
    {
        var intent = await IntentAsync(owned);

        intent.RecoveryOwnerId.ShouldNotBeNull("the faulted write did not land, so the claim stays leased until its lease expires");
        intent.RecoveryAttemptCount.ShouldBe(1);
        intent.State.ShouldBe(AgentRunLogCaptureIntentState.Expected);
        intent.LastErrorCode.ShouldBeNull();
    }

    /// <summary>The neighbour ahead of this test's intent went through the same seam: it was claimed, settled, and never logged.</summary>
    private async Task ShouldHaveLeftTheNeighbourAloneAsync(SettlementScene scene, RecordedRecoveryLog log)
    {
        var stranger = await IntentAsync(scene.Neighbour);

        stranger.RecoveryAttemptCount.ShouldBeGreaterThan(0, "the neighbour ahead of this intent was never claimed, so nothing showed the seam leaves it alone");
        stranger.RecoveryOwnerId.ShouldBeNull("the seam faulted a neighbour's settlement; it must fault only this test's intent");
        log.About(stranger.Id).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_recovery_step_the_database_refuses_is_logged_with_its_cause_and_still_settles_as_a_typed_retry()
    {
        // An error the recovery step raises becomes a retry whose last_error_code, recovery-operation-exception, names no
        // cause. A deterministic refusal then repeats on every retry until the intent is exhausted into
        // ExternalStateIndeterminate, and without the cause in the log nothing says why.
        var scene = await SeedOwnedIntentBehindDueNeighbourAsync();
        var refusal = new RefusedCaptureRecoveryReadInterceptor(scene.Owned.TeamId);
        var log = new RecordedRecoveryLog();
        var recovery = Recovery(scene.Logs, interceptor: refusal, logger: log);

        var entry = await ShouldSettleTheStepsRetryAloneAsync(scene, recovery, log, AgentRunLogCaptureIntentState.Expected, "recovery-operation-exception");

        refusal.Refused.ShouldBeTrue("the seam never refused this intent's recovery read, so nothing was raised to log");
        entry.Properties["SqlState"].ShouldBe(PostgresErrorCodes.RaiseException);
        entry.Properties["MessageText"].ShouldBeOfType<string>().ShouldContain(scene.Owned.TeamId.ToString());
        entry.Exception.ShouldBeOfType<PostgresException>();
    }

    [Theory]
    [InlineData(8, AgentRunLogCaptureIntentState.Expected, "recovery-operation-exception")]         // the settlement writes the step's retry
    [InlineData(1, AgentRunLogCaptureIntentState.ExternalStateIndeterminate, "recovery-exhausted")] // the settlement exhausts the step's retry on its last attempt
    public async Task A_recovery_step_that_fails_outside_the_database_is_logged_without_a_database_cause_and_its_settlement_decides_the_retry(int maxAttempts, AgentRunLogCaptureIntentState settledState, string settledCode)
    {
        // A provider fault inside CompleteAsync carries no SQLSTATE. The exception is then the whole cause, and naming the
        // absent database fields must not throw from inside the catch, where it would fault the wave and stop its claims.
        // A deterministic fault repeats until the settlement exhausts the intent on its last attempt, and the line is
        // written before that settlement runs, so it must not promise that attempt another retry.
        var scene = await SeedOwnedIntentBehindDueNeighbourAsync();
        var log = new RecordedRecoveryLog();
        var recovery = Recovery(new ThrowingCompleteLogService(scene.Logs, scene.Owned.AgentRunId), new RecoveryTestOptions { MaxAttempts = maxAttempts }, logger: log);

        var entry = await ShouldSettleTheStepsRetryAloneAsync(scene, recovery, log, settledState, settledCode);

        entry.Properties["SqlState"].ShouldBeNull();
        entry.Properties["MessageText"].ShouldBeNull();
        entry.Exception.ShouldBeOfType<InvalidOperationException>().Message.ShouldContain(scene.Owned.AgentRunId.ToString());
    }

    /// <summary>
    /// Reconciles until THIS test's intent settles the typed retry an unexpected recovery error becomes, as that retry or as
    /// what its settlement replaced it with, asserts that the settlement released its claim as it did before the error was
    /// logged and that the seam left the neighbour alone, then returns the one entry that names the intent.
    /// </summary>
    private async Task<RecordedEntry> ShouldSettleTheStepsRetryAloneAsync(SettlementScene scene, AgentRunLogCaptureRecoveryService recovery, RecordedRecoveryLog log, AgentRunLogCaptureIntentState settledState, string settledCode)
    {
        var intent = await ReconcileUntilAsync(recovery, scene.Owned, value => value.LastErrorCode == settledCode, $"settled the typed retry an unexpected recovery error becomes as {settledState}/{settledCode}");

        intent.State.ShouldBe(settledState);
        intent.RecoveryOwnerId.ShouldBeNull("a settlement releases its claim");
        intent.RecoveryAttemptCount.ShouldBe(1);
        await ShouldHaveLeftTheNeighbourAloneAsync(scene, log);

        var entry = log.About(scene.IntentId).ShouldHaveSingleItem("the unexpected recovery error must be logged once, naming its intent");
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Properties["RunId"].ShouldBe(scene.Owned.AgentRunId);

        // The line is written before the settlement runs, so it names the step's outcome and leaves the rest to the settlement.
        entry.Properties["Outcome"].ShouldBe(AgentRunLogCaptureIntentState.Expected);
        entry.Properties["OutcomeCode"].ShouldBe("recovery-operation-exception");
        entry.Properties["{OriginalFormat}"].ShouldBeOfType<string>().ShouldContain("unless the settlement supersedes or exhausts the intent");

        return entry;
    }

    [Theory]
    [InlineData(false, "lease-expired")] // nothing re-claimed the intent: the claim is still this wave's, but its lease is gone
    [InlineData(true, "reclaimed")]      // another worker re-claimed the intent once the lease expired, and settled it first
    public async Task A_settlement_that_outlived_its_lease_is_logged_with_why_and_counted_in_the_summary_its_wave_logs(bool reclaimedByAnotherWorker, string cause)
    {
        // A lease outlives both bounded steps of a claim, so a live worker loses one only when a step overruns the bound
        // its cancellation sets — here a provider call that ignores cancellation. The fence discards the late settlement,
        // which is right, but it was counted as a lost lease with no log line, and nothing read that tally either.
        var scene = await SeedOwnedIntentBehindDueNeighbourAsync();
        var stall = new StallFirstOwnCompleteLogService(scene.Logs, scene.Owned.AgentRunId);
        var log = new RecordedRecoveryLog();
        var recovery = Recovery(stall, logger: log);
        var wave = await ReconcileUntilPausedAsync(recovery, stall.Entered, scene.Owned);

        if (reclaimedByAnotherWorker)
            await ReconcileUntilAsync(Recovery(scene.Logs), scene.Owned, value => value.State == AgentRunLogCaptureIntentState.Completed, "re-claimed and completed by another worker once the stalled claim's lease expired", LeaseWait);
        else
            await WaitForLeaseToExpireAsync(scene.Owned);

        stall.Release();
        var summary = await wave;

        summary.LostLease.ShouldBeGreaterThanOrEqualTo(1, "the late settlement is discarded and counted as a lost lease");
        log.Summaries.Last().ShouldBe(summary, "the wave must log the tally it returns; nothing else reads its lost leases");
        await ShouldHaveLeftTheNeighbourAloneAsync(scene, log);

        var entry = log.About(scene.IntentId).ShouldHaveSingleItem("the lost claim must be logged once, naming its intent");
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Properties["RunId"].ShouldBe(scene.Owned.AgentRunId);
        entry.Properties["Cause"].ShouldBe(cause);
        entry.Properties["Outcome"].ShouldBe(AgentRunLogCaptureIntentState.SourceFinalized);
        entry.Properties["OutcomeCode"].ShouldBe("complete-backend-unavailable");
        entry.Exception.ShouldBeNull();
    }

    [Theory]
    [InlineData(8, AgentRunLogCaptureIntentState.SourceFinalized, "complete-backend-unavailable")] // the observed retry is the write discarded
    [InlineData(1, AgentRunLogCaptureIntentState.ExternalStateIndeterminate, "recovery-exhausted")] // the settlement exhausts the observed retry, and that write is discarded
    public async Task A_settlement_whose_row_changed_under_its_lock_is_logged_with_the_write_it_discarded_and_still_waits_out_its_lease(int maxAttempts, AgentRunLogCaptureIntentState discardedState, string discardedCode)
    {
        // The settlement locks the intent before it reads it, so its write matches no row only if the row changed under
        // that lock — a contract the service relies on and nothing else checks. The write is counted as a lost lease, and
        // its claim stays leased until the lease expires, with no log line. A settlement can replace the outcome it
        // observed before it writes, so the line must name the write it discarded, not the outcome it observed.
        var scene = await SeedOwnedIntentBehindDueNeighbourAsync();
        var stale = new RefusedCaptureSettlementInterceptor(scene.IntentId, "xmin");
        var log = new RecordedRecoveryLog();
        var recovery = Recovery(new AlwaysRetryableCompleteLogService(scene.Logs), new RecoveryTestOptions { MaxAttempts = maxAttempts }, stale, log);

        var summary = await ReconcileUntilFaultedAsync(recovery, () => stale.Refused, scene.Owned);

        summary.LostLease.ShouldBeGreaterThanOrEqualTo(1, "a write that matches no row is counted as a lost lease");
        log.Summaries.Last().ShouldBe(summary, "the wave must log the tally it returns; nothing else reads its lost leases");
        await ShouldBeLeftLeasedAndUnwrittenAsync(scene.Owned);
        await ShouldHaveLeftTheNeighbourAloneAsync(scene, log);

        var entry = log.About(scene.IntentId).ShouldHaveSingleItem("the lost claim must be logged once, naming its intent");
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Properties["RunId"].ShouldBe(scene.Owned.AgentRunId);
        entry.Properties["Cause"].ShouldBe("row-version-changed");
        entry.Properties["Outcome"].ShouldBe(discardedState);
        entry.Properties["OutcomeCode"].ShouldBe(discardedCode);
        entry.Exception.ShouldBeOfType<DbUpdateConcurrencyException>();
    }

    [Fact]
    public async Task A_wave_logs_one_summary_when_it_claimed_work_and_none_when_it_claimed_nothing()
    {
        // The recurring job discards the summary, so the wave's own log line is the only place its tally is read. A wave
        // that claimed nothing has nothing to report, and one line a minute from every idle worker would bury the rest.
        var owned = await SeedWorldAsync();
        var logs = LogService();
        var sessionId = Guid.NewGuid();
        await Recovery(logs).DeclareAsync(Declaration(owned, sessionId, 7, AgentRunLogKinds.StandardOutput), CancellationToken.None);
        await SeedFinalizedTerminalStreamAsync(owned, logs, sessionId);
        var log = new RecordedRecoveryLog();
        var recovery = Recovery(logs, logger: log);

        var claimingWaves = await ReconcileUntilIdleAsync(recovery, owned);

        claimingWaves.ShouldBeGreaterThanOrEqualTo(1, "this test's own due intent was never claimed, so no wave had work to report");
        log.Summaries.Count.ShouldBe(claimingWaves, "every wave that claimed work logs its summary once, and the idle wave logs none");
    }

    /// <summary>
    /// Reconciles until a wave claims nothing, after THIS test's intent has been claimed, and returns how many waves claimed
    /// work first. Waves are deployment-wide, so earlier tests' due intents are claimed too, and each settles terminal or
    /// schedules its retry past the next wave's cutoff, so a wave with nothing due follows.
    /// </summary>
    private async Task<int> ReconcileUntilIdleAsync(AgentRunLogCaptureRecoveryService recovery, World world)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        var claimingWaves = 0;
        (await IntentAsync(world)).RecoveryAttemptCount.ShouldBe(0, "the intent was claimed before any wave was started to claim it");

        while (DateTimeOffset.UtcNow < deadline)
        {
            var summary = await recovery.ReconcileAsync(CancellationToken.None);

            if (summary.Claimed == 0 && (await IntentAsync(world)).RecoveryAttemptCount > 0) return claimingWaves;

            if (summary.Claimed > 0) claimingWaves++;
        }

        throw new Xunit.Sdk.XunitException(
            $"No reconcile wave claimed nothing after agent run {world.AgentRunId}'s capture intent was claimed, across {claimingWaves} claiming wave(s). "
            + "Reconcile waves are deployment-wide, so check whether an earlier test left intents that fall due again before every next wave's cutoff.");
    }

    /// <summary>How long a test waits on a 6 s recovery lease to expire, with room for a loaded database.</summary>
    private static readonly TimeSpan LeaseWait = TimeSpan.FromSeconds(20);

    /// <summary>Polls the database clock until THIS test's claimed intent's recovery lease has expired.</summary>
    private async Task WaitForLeaseToExpireAsync(World world)
    {
        var deadline = DateTimeOffset.UtcNow + LeaseWait;
        (await LeaseExpiredAsync(world)).ShouldBeFalse("waiting for the lease to expire is meaningless when it had expired before the wait began");

        while (!await LeaseExpiredAsync(world))
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new Xunit.Sdk.XunitException($"The recovery lease on agent run {world.AgentRunId}'s capture intent never expired by the database clock; check its recovery_lease_expires_at against clock_timestamp().");

            await Task.Delay(50);
        }
    }

    private async Task<bool> LeaseExpiredAsync(World world)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().Database
            .SqlQuery<bool>($"SELECT COALESCE(recovery_lease_expires_at <= clock_timestamp(), FALSE) AS \"Value\" FROM agent_run_log_capture_intent WHERE agent_run_id = {world.AgentRunId}").SingleAsync();
    }

    [Fact]
    public async Task Terminal_grace_uses_database_observation_time_not_positive_or_negative_application_clock_skew()
    {
        var early = await SeedWorldAsync();
        var late = await SeedWorldAsync();
        var recovery = Recovery(LogService(), new RecoveryTestOptions { TerminalGrace = TimeSpan.FromMilliseconds(60) });
        await recovery.DeclareAsync(Declaration(early, Guid.NewGuid(), 7, AgentRunLogKinds.StandardOutput), CancellationToken.None);
        await recovery.DeclareAsync(Declaration(late, Guid.NewGuid(), 7, AgentRunLogKinds.StandardOutput), CancellationToken.None);
        await MarkTerminalAsync(early, AgentRunStatus.Succeeded, "{}", DateTimeOffset.UtcNow.AddDays(-3));
        await MarkTerminalAsync(late, AgentRunStatus.Succeeded, "{}", DateTimeOffset.UtcNow.AddDays(3));

        await recovery.ReconcileAsync(CancellationToken.None);

        using (var scope = _fixture.BeginScope())
        {
            var ids = new[] { early.AgentRunId, late.AgentRunId };
            var armed = await scope.Resolve<CodeSpaceDbContext>().AgentRunLogCaptureIntent.Where(value => ids.Contains(value.AgentRunId)).ToListAsync();
            armed.ShouldAllBe(value => value.State == AgentRunLogCaptureIntentState.Expected && value.TerminalObservedAt != null && value.LastErrorCode == "terminal-grace-armed");
        }
        await Task.Delay(90);
        await recovery.ReconcileAsync(CancellationToken.None);

        using var finalScope = _fixture.BeginScope();
        var runIds = new[] { early.AgentRunId, late.AgentRunId };
        var terminal = await finalScope.Resolve<CodeSpaceDbContext>().AgentRunLogCaptureIntent.Where(value => runIds.Contains(value.AgentRunId)).ToListAsync();
        terminal.ShouldAllBe(value => value.State == AgentRunLogCaptureIntentState.CaptureFailed && value.LastErrorCode == "expected-stream-missing");
    }

    [Fact]
    public async Task A_final_drain_that_arrives_during_DB_clock_grace_completes_instead_of_becoming_a_false_capture_failure()
    {
        var world = await SeedWorldAsync();
        var logs = LogService();
        var recovery = Recovery(logs, new RecoveryTestOptions { TerminalGrace = TimeSpan.FromMilliseconds(60) });
        var sessionId = Guid.NewGuid();
        await recovery.DeclareAsync(Declaration(world, sessionId, 7, AgentRunLogKinds.StandardOutput), CancellationToken.None);
        var opened = (await logs.OpenAsync(Open(world, sessionId, AgentRunLogKinds.StandardOutput), CancellationToken.None)).ShouldBeOfType<AgentRunLogOpenResult.Opened>();
        await MarkTerminalAsync(world, AgentRunStatus.Succeeded, "{\"status\":\"Succeeded\"}");

        await recovery.ReconcileAsync(CancellationToken.None);
        await logs.FinalizeSourceAsync(new AgentRunLogFinalizeSourceRequest
        {
            TeamId = world.TeamId, AgentRunId = world.AgentRunId, StreamId = opened.Metadata.StreamId,
            WorkerFenceEpoch = 7, CaptureSessionId = sessionId, ExpectedRevision = opened.Metadata.Revision, ExpectedSourceOffsetBytes = 0,
        }, CancellationToken.None);
        await Task.Delay(90);
        await recovery.ReconcileAsync(CancellationToken.None);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        (await db.AgentRunLogCaptureIntent.SingleAsync(value => value.AgentRunId == world.AgentRunId)).State.ShouldBe(AgentRunLogCaptureIntentState.Completed);
        (await db.AgentRunLogStream.SingleAsync(value => value.Id == opened.Metadata.StreamId)).State.ShouldBe(AgentRunLogStreamState.Completed);
    }

    [Fact]
    public async Task Transient_fast_path_completion_rejection_remains_open_and_later_reconciles_to_completed()
    {
        var world = await SeedWorldAsync();
        var logs = LogService();
        var recovery = Recovery(logs);
        var sessionId = Guid.NewGuid();
        await recovery.DeclareAsync(Declaration(world, sessionId, 7, AgentRunLogKinds.StandardOutput), CancellationToken.None);
        var opened = (await logs.OpenAsync(Open(world, sessionId, AgentRunLogKinds.StandardOutput), CancellationToken.None)).ShouldBeOfType<AgentRunLogOpenResult.Opened>();
        await logs.FinalizeSourceAsync(new AgentRunLogFinalizeSourceRequest
        {
            TeamId = world.TeamId, AgentRunId = world.AgentRunId, StreamId = opened.Metadata.StreamId,
            WorkerFenceEpoch = 7, CaptureSessionId = sessionId, ExpectedRevision = opened.Metadata.Revision, ExpectedSourceOffsetBytes = 0,
        }, CancellationToken.None);
        await MarkTerminalAsync(world, AgentRunStatus.Succeeded, "{\"status\":\"Succeeded\"}");
        var transient = new RejectCompleteOnceLogService(logs);
        var bridge = new AgentRunLogCaptureBridge(transient, new UnusedStorageResolver(), recovery, NullLogger<AgentRunLogCaptureBridge>.Instance);

        await bridge.CompleteRunAsync(world.TeamId, world.AgentRunId, 7, CancellationToken.None);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            (await db.AgentRunLogStream.SingleAsync(value => value.Id == opened.Metadata.StreamId)).State.ShouldBe(AgentRunLogStreamState.Open);
            (await db.AgentRunLogCaptureIntent.SingleAsync(value => value.AgentRunId == world.AgentRunId)).State.ShouldBe(AgentRunLogCaptureIntentState.Expected);
        }
        await recovery.ReconcileAsync(CancellationToken.None);

        using var finalScope = _fixture.BeginScope();
        var finalDb = finalScope.Resolve<CodeSpaceDbContext>();
        (await finalDb.AgentRunLogStream.SingleAsync(value => value.Id == opened.Metadata.StreamId)).State.ShouldBe(AgentRunLogStreamState.Completed);
        (await finalDb.AgentRunLogCaptureIntent.SingleAsync(value => value.AgentRunId == world.AgentRunId)).State.ShouldBe(AgentRunLogCaptureIntentState.Completed);
    }

    /// <summary>
    /// Reconciles until THIS test's intent satisfies <paramref name="settled"/>, then returns it.
    ///
    /// <para>A single wave is not something a test may assume reaches its target. The sweep takes no team and is
    /// bounded, so every intent left due by every test that ran before this one competes for the same slots. Waiting
    /// on the target's own row is the only formulation that states what these tests mean, and it stops depending on
    /// how many other tests the suite has accumulated.</para>
    ///
    /// <para><paramref name="settled"/> must be FALSE when this is called, and that is asserted rather than assumed.
    /// A predicate the row already satisfies — waiting for an intent to be <c>Expected</c> when it starts
    /// <c>Expected</c> — returns on the first look and waits for nothing, which reads as a pass whether or not any
    /// wave ever touched the target. Name the thing the work PRODUCES, not the state it began in.</para>
    /// </summary>
    private async Task<AgentRunLogCaptureIntent> ReconcileUntilAsync(AgentRunLogCaptureRecoveryService recovery, World world, Func<AgentRunLogCaptureIntent, bool> settled, string expectation, TimeSpan? within = null)
    {
        var deadline = DateTimeOffset.UtcNow + (within ?? TimeSpan.FromSeconds(10));
        var seen = await IntentAsync(world);
        settled(seen).ShouldBeFalse($"waiting for '{expectation}' is meaningless because the intent already satisfies it before any reconcile has run");

        while (DateTimeOffset.UtcNow < deadline)
        {
            await recovery.ReconcileAsync(CancellationToken.None);
            seen = await IntentAsync(world);

            if (settled(seen)) return seen;

            await Task.Delay(25);
        }

        throw new Xunit.Sdk.XunitException(
            $"The capture intent for agent run {world.AgentRunId} never reached '{expectation}' (last seen {seen.State}, "
            + $"attempts {seen.RecoveryAttemptCount}, last error {seen.LastErrorCode ?? "none"}). "
            + "Reconcile waves are deployment-wide and bounded, so check whether earlier tests left enough due intents to crowd this one out.");
    }

    /// <summary>
    /// Starts reconcile waves until one pauses inside THIS test's gated provider call — <paramref name="paused"/> is the
    /// gate's signal — and returns that wave still in flight. A wave is deployment-wide and bounded, so one start is not
    /// guaranteed to reach the target; a wave that finishes without pausing missed it, and the next one is started.
    ///
    /// <para>The gate must still be shut when this is called, and that is asserted rather than assumed: a gate that
    /// opened earlier — on a neighbour's call, or on this test's call in an earlier wave — returns at once, and whatever
    /// the caller checks or changes next lands outside the pause it meant to hold.</para>
    /// </summary>
    private async Task<Task<AgentRunLogCaptureRecoverySummary>> ReconcileUntilPausedAsync(AgentRunLogCaptureRecoveryService recovery, Task paused, World world)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        paused.IsCompleted.ShouldBeFalse("the gate opened before any wave was started to reach this test's own provider call");

        while (DateTimeOffset.UtcNow < deadline)
        {
            var wave = recovery.ReconcileAsync(CancellationToken.None);

            if (await Task.WhenAny(paused, wave) == paused) return wave;

            await wave;
        }

        var seen = await IntentAsync(world);
        throw new Xunit.Sdk.XunitException(
            $"No reconcile wave paused inside a gated provider call for agent run {world.AgentRunId} (intent last seen {seen.State}, "
            + $"attempts {seen.RecoveryAttemptCount}, last error {seen.LastErrorCode ?? "none"}). "
            + "Reconcile waves are deployment-wide and bounded, so check whether earlier tests left enough due intents to crowd this one out.");
    }

    /// <summary>
    /// Releases a wave held inside a gated provider call, then reconciles until every intent of <paramref name="worlds"/>
    /// is Completed, and returns them. The released wave is bounded, so it may run out of budget before reaching them all;
    /// later waves finish the rest.
    ///
    /// <para>None of the intents may be Completed while the wave is still held, and that is asserted before the release:
    /// the released wave usually finishes them itself, so the first look after it already sees the result, and only this
    /// guard shows the result was produced after the pause rather than held before it.</para>
    /// </summary>
    private async Task<AgentRunLogCaptureIntent[]> ReleaseAndReconcileUntilCompletedAsync(AgentRunLogCaptureRecoveryService recovery, Task held, Action release, params World[] worlds)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        var seen = await Task.WhenAll(worlds.Select(IntentAsync));
        seen.ShouldAllBe(value => value.State != AgentRunLogCaptureIntentState.Completed, "an owned intent was already Completed while the wave was still held");

        release();
        await held;
        seen = await Task.WhenAll(worlds.Select(IntentAsync));

        while (!seen.All(value => value.State == AgentRunLogCaptureIntentState.Completed))
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new Xunit.Sdk.XunitException(
                    "The owned capture intents never all reached Completed after the held wave was released (last seen "
                    + string.Join(", ", seen.Select(value => $"{value.AgentRunId}: {value.State}, attempts {value.RecoveryAttemptCount}, last error {value.LastErrorCode ?? "none"}"))
                    + "). Reconcile waves are deployment-wide and bounded, so check whether earlier tests left enough due intents to crowd these out.");

            await recovery.ReconcileAsync(CancellationToken.None);
            seen = await Task.WhenAll(worlds.Select(IntentAsync));
        }

        return seen;
    }

    /// <summary>
    /// Reconciles until a wave reaches THIS test's faulted settlement — <paramref name="faulted"/> is the seam's signal —
    /// and returns that wave's summary. A wave is deployment-wide and bounded, so one start is not guaranteed to reach it.
    /// </summary>
    private static async Task<AgentRunLogCaptureRecoverySummary> ReconcileUntilFaultedAsync(AgentRunLogCaptureRecoveryService recovery, Func<bool> faulted, World world)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        faulted().ShouldBeFalse("the seam fired before any wave was started to reach this test's own settlement");

        while (DateTimeOffset.UtcNow < deadline)
        {
            var summary = await recovery.ReconcileAsync(CancellationToken.None);

            if (faulted()) return summary;

            await Task.Delay(25);
        }

        throw new Xunit.Sdk.XunitException(
            $"No reconcile wave reached the settlement of agent run {world.AgentRunId}'s capture intent, so its seam never faulted it. "
            + "Reconcile waves are deployment-wide and bounded, so check whether earlier tests left enough due intents to crowd this one out, "
            + "then whether the seam still recognises the settlement's SQL.");
    }

    private async Task<AgentRunLogCaptureIntent> IntentAsync(World world)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().AgentRunLogCaptureIntent.AsNoTracking()
            .SingleAsync(value => value.AgentRunId == world.AgentRunId);
    }

    private AgentRunLogCaptureRecoveryService Recovery(IAgentRunLogService logs, RecoveryTestOptions? options = null, IInterceptor? interceptor = null, ILogger<AgentRunLogCaptureRecoveryService>? logger = null)
    {
        options ??= new RecoveryTestOptions();
        using var scope = _fixture.BeginScope();
        var dbOptions = scope.Resolve<DbContextOptions<CodeSpaceDbContext>>();
        if (interceptor != null) dbOptions = new DbContextOptionsBuilder<CodeSpaceDbContext>(dbOptions).AddInterceptors(interceptor).Options;

        return new AgentRunLogCaptureRecoveryService(dbOptions, logs,
            new AgentRunLogCaptureRecoveryOptions(20, options.MaxConcurrency, TimeSpan.FromSeconds(6), options.OperationTimeout,
                new AgentRunLogCaptureRetryPolicy(options.BaseDelay, options.MaxDelay, options.MaxAttempts, options.MaxAge, options.TerminalGrace)), logger);
    }

    private sealed record RecoveryTestOptions
    {
        public int MaxConcurrency { get; init; } = 4;
        public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(2);
        public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(100);
        public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(1);
        public int MaxAttempts { get; init; } = 8;
        public TimeSpan MaxAge { get; init; } = TimeSpan.FromMinutes(5);
        public TimeSpan TerminalGrace { get; init; } = TimeSpan.FromSeconds(1);
    }

    private AgentRunLogService LogService()
    {
        using var scope = _fixture.BeginScope();
        return new AgentRunLogService(scope.Resolve<DbContextOptions<CodeSpaceDbContext>>(), new EmptyCas(), TimeProvider.System);
    }

    private async Task<World> SeedWorldAsync()
    {
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.User.Add(new User { Id = actorId, Email = $"log-recovery-{actorId:N}@test.local", Name = "Log Recovery" });
        db.Team.Add(new Team { Id = teamId, Slug = $"log-recovery-{teamId:N}", Name = "Log Recovery", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = actorId, Role = TeamRole.Owner });
        await db.SaveChangesAsync();
        db.AgentRun.Add(new AgentRun
        {
            Id = runId, TeamId = teamId, Harness = "test-harness", Status = AgentRunStatus.Running, TaskJson = "{}", FenceEpoch = 7,
            CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        });
        await db.SaveChangesAsync();
        return new World(teamId, actorId, runId);
    }

    private async Task MarkTerminalAsync(World world, AgentRunStatus status, string resultJson, DateTimeOffset? completedAt = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var run = await db.AgentRun.SingleAsync(value => value.Id == world.AgentRunId);
        run.Status = status;
        run.ResultJson = resultJson;
        run.CompletedAt = completedAt ?? DateTimeOffset.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();
    }

    private async Task RaiseFenceAsync(World world, long fence)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        await db.AgentRun.Where(value => value.Id == world.AgentRunId).ExecuteUpdateAsync(update => update.SetProperty(value => value.FenceEpoch, fence));
    }

    /// <summary>
    /// Seeds another tenant's terminal run whose open stream the next wave will fail through FailCaptureAsync — the shape
    /// an earlier test, or another tenant in production, leaves behind. Recovery takes no team, so that effect runs through
    /// whatever log service the next wave's recovery holds, including a test's gate.
    /// </summary>
    private async Task<World> SeedDueOpenStreamNeighbourAsync()
    {
        var neighbour = await SeedWorldAsync();
        var logs = LogService();
        var recovery = Recovery(logs, new RecoveryTestOptions { TerminalGrace = TimeSpan.Zero });
        var sessionId = Guid.NewGuid();
        await recovery.DeclareAsync(Declaration(neighbour, sessionId, 7, AgentRunLogKinds.StandardOutput), CancellationToken.None);
        (await logs.OpenAsync(Open(neighbour, sessionId, AgentRunLogKinds.StandardOutput), CancellationToken.None)).ShouldBeOfType<AgentRunLogOpenResult.Opened>();
        await MarkTerminalAsync(neighbour, AgentRunStatus.Succeeded, "{}");

        await ReconcileUntilAsync(recovery, neighbour, value => value.TerminalObservedAt != null, "neighbour terminal grace armed");

        return neighbour;
    }

    /// <summary>
    /// Seeds another tenant's terminal run whose finalized stream the next wave will complete through CompleteAsync. It is
    /// due at once and ahead of anything seeded after it, so that effect runs first through whatever log service the next
    /// wave's recovery holds, including a test's gate.
    /// </summary>
    private async Task<World> SeedDueFinalizedStreamNeighbourAsync()
    {
        var neighbour = await SeedWorldAsync();
        var logs = LogService();
        var sessionId = Guid.NewGuid();
        await Recovery(logs).DeclareAsync(Declaration(neighbour, sessionId, 7, AgentRunLogKinds.StandardOutput), CancellationToken.None);
        await SeedFinalizedTerminalStreamAsync(neighbour, logs, sessionId);

        return neighbour;
    }

    private async Task SeedFinalizedTerminalStreamAsync(World world, IAgentRunLogService logs, Guid sessionId)
    {
        var opened = (await logs.OpenAsync(Open(world, sessionId, AgentRunLogKinds.StandardOutput), CancellationToken.None)).ShouldBeOfType<AgentRunLogOpenResult.Opened>();
        await logs.FinalizeSourceAsync(new AgentRunLogFinalizeSourceRequest
        {
            TeamId = world.TeamId, AgentRunId = world.AgentRunId, StreamId = opened.Metadata.StreamId,
            WorkerFenceEpoch = 7, CaptureSessionId = sessionId, ExpectedRevision = opened.Metadata.Revision, ExpectedSourceOffsetBytes = 0,
        }, CancellationToken.None);
        await MarkTerminalAsync(world, AgentRunStatus.Succeeded, "{\"status\":\"Succeeded\"}");
    }

    private static AgentRunLogCaptureDeclarationRequest Declaration(World world, Guid sessionId, long fence, params string[] kinds) => new()
    {
        TeamId = world.TeamId, AgentRunId = world.AgentRunId, WorkerFenceEpoch = fence, CaptureSessionId = sessionId,
        Streams = kinds.Select(kind => new AgentRunLogExpectedStream(kind, "text/plain", "utf-8", "test-spool/v1")).ToArray(),
    };

    private static AgentRunLogOpenRequest Open(World world, Guid sessionId, string kind, long fence = 7) => new()
    {
        TeamId = world.TeamId, AgentRunId = world.AgentRunId, WorkerFenceEpoch = fence, CaptureSessionId = sessionId,
        StreamKind = kind, ContentType = "text/plain", ContentEncoding = "utf-8", CaptureSource = "test-spool/v1",
    };

    private sealed record World(Guid TeamId, Guid ActorId, Guid AgentRunId);

    private sealed record SettlementScene(World Neighbour, World Owned, Guid IntentId, IAgentRunLogService Logs);

    /// <summary>
    /// The recovery's log entries, kept as their structured properties rather than a rendered string, so what an operator
    /// is handed — which intent, which run, which database refusal — is the thing under test.
    /// </summary>
    private sealed class RecordedRecoveryLog : ILogger<AgentRunLogCaptureRecoveryService>
    {
        private readonly ConcurrentQueue<RecordedEntry> _entries = [];

        /// <summary>The entries naming one intent. The sweep is deployment-wide, so whatever else it met in this shared database is not this test's business.</summary>
        public IReadOnlyList<RecordedEntry> About(Guid intentId) => _entries.Where(entry => Equals(entry.Properties.GetValueOrDefault("IntentId"), intentId)).ToList();

        /// <summary>The wave summaries, in the order the waves logged them, rebuilt from their structured properties.</summary>
        public IReadOnlyList<AgentRunLogCaptureRecoverySummary> Summaries => _entries.Where(entry => entry.Properties.ContainsKey("Claimed")).Select(Summary).ToList();

        private static AgentRunLogCaptureRecoverySummary Summary(RecordedEntry entry) => new(Count(entry, "Claimed"), Count(entry, "Completed"), Count(entry, "CaptureFailed"), Count(entry, "Superseded"), Count(entry, "Retried"), Count(entry, "LostLease"))
        {
            ExternalStateIndeterminate = Count(entry, "ExternalStateIndeterminate"),
        };

        private static int Count(RecordedEntry entry, string name) => entry.Properties[name].ShouldBeOfType<int>();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is not IReadOnlyList<KeyValuePair<string, object?>> properties) return;

            _entries.Enqueue(new RecordedEntry(logLevel, properties.ToDictionary(property => property.Key, property => property.Value, StringComparer.Ordinal), exception));
        }
    }

    private sealed record RecordedEntry(LogLevel Level, IReadOnlyDictionary<string, object?> Properties, Exception? Exception);

    private sealed class EmptyCas : IArtifactCasRuntimeCoordinator
    {
        public Task<ArtifactCasTransferResult> PutAsync(ArtifactCasTransferRequest request, CancellationToken cancellationToken) => Task.FromResult<ArtifactCasTransferResult>(new ArtifactCasTransferResult.Rejected(null, new ArtifactCasProblem(ArtifactCasProblemCode.Unsupported, false)));
        public Task<ArtifactCasReadResult> OpenReadAsync(ArtifactCasReadRequest request, CancellationToken cancellationToken) => Task.FromResult<ArtifactCasReadResult>(new ArtifactCasReadResult.Unavailable(new ArtifactCasProblem(ArtifactCasProblemCode.TargetMissing, false)));
    }

    private sealed class UnusedStorageResolver : IAgentRunLogStorageResolver
    {
        public Task<AgentRunLogStorageResolution> ResolveAsync(Guid teamId, CancellationToken cancellationToken) =>
            Task.FromResult<AgentRunLogStorageResolution>(new AgentRunLogStorageResolution.Unavailable(AgentRunLogStorageProblemCode.Missing));
    }

    private sealed class RejectCompleteOnceLogService(IAgentRunLogService inner) : IAgentRunLogService
    {
        private int _remaining = 1;
        public Task<AgentRunLogOpenResult> OpenAsync(AgentRunLogOpenRequest request, CancellationToken cancellationToken) => inner.OpenAsync(request, cancellationToken);
        public Task<AgentRunLogAppendResult> AppendAsync(AgentRunLogAppendRequest request, CancellationToken cancellationToken) => inner.AppendAsync(request, cancellationToken);
        public Task<AgentRunLogFinalizeSourceResult> FinalizeSourceAsync(AgentRunLogFinalizeSourceRequest request, CancellationToken cancellationToken) => inner.FinalizeSourceAsync(request, cancellationToken);
        public Task<AgentRunLogCompleteResult> CompleteAsync(AgentRunLogCompleteRequest request, CancellationToken cancellationToken) => Interlocked.Exchange(ref _remaining, 0) == 1
            ? Task.FromResult<AgentRunLogCompleteResult>(new AgentRunLogCompleteResult.Rejected(new AgentRunLogProblem(AgentRunLogProblemCode.BackendUnavailable, true)))
            : inner.CompleteAsync(request, cancellationToken);
        public Task<AgentRunLogFailCaptureResult> FailCaptureAsync(AgentRunLogFailCaptureRequest request, CancellationToken cancellationToken) => inner.FailCaptureAsync(request, cancellationToken);
        public Task<int> RecordOwnerLossAsync(AgentRunLogOwnerLossRequest request, CancellationToken cancellationToken) => inner.RecordOwnerLossAsync(request, cancellationToken);
        public Task<AgentRunLogMetadataResult> GetMetadataAsync(Guid teamId, Guid streamId, CancellationToken cancellationToken) => inner.GetMetadataAsync(teamId, streamId, cancellationToken);
        public Task<IReadOnlyList<AgentRunLogMetadata>> ListMetadataAsync(Guid teamId, Guid agentRunId, CancellationToken cancellationToken) => inner.ListMetadataAsync(teamId, agentRunId, cancellationToken);
        public Task<IReadOnlyList<AgentRunLogCaptureHead>> ListCaptureHeadsAsync(Guid teamId, Guid agentRunId, CancellationToken cancellationToken) => inner.ListCaptureHeadsAsync(teamId, agentRunId, cancellationToken);
        public Task<AgentRunLogRangeResult> ReadRangeAsync(AgentRunLogRangeRequest request, CancellationToken cancellationToken) => inner.ReadRangeAsync(request, cancellationToken);
    }

    private sealed class AlwaysRetryableCompleteLogService(IAgentRunLogService inner) : IAgentRunLogService
    {
        public Task<AgentRunLogOpenResult> OpenAsync(AgentRunLogOpenRequest request, CancellationToken cancellationToken) => inner.OpenAsync(request, cancellationToken);
        public Task<AgentRunLogAppendResult> AppendAsync(AgentRunLogAppendRequest request, CancellationToken cancellationToken) => inner.AppendAsync(request, cancellationToken);
        public Task<AgentRunLogFinalizeSourceResult> FinalizeSourceAsync(AgentRunLogFinalizeSourceRequest request, CancellationToken cancellationToken) => inner.FinalizeSourceAsync(request, cancellationToken);
        public Task<AgentRunLogCompleteResult> CompleteAsync(AgentRunLogCompleteRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<AgentRunLogCompleteResult>(new AgentRunLogCompleteResult.Rejected(new AgentRunLogProblem(AgentRunLogProblemCode.BackendUnavailable, true)));
        public Task<AgentRunLogFailCaptureResult> FailCaptureAsync(AgentRunLogFailCaptureRequest request, CancellationToken cancellationToken) => inner.FailCaptureAsync(request, cancellationToken);
        public Task<int> RecordOwnerLossAsync(AgentRunLogOwnerLossRequest request, CancellationToken cancellationToken) => inner.RecordOwnerLossAsync(request, cancellationToken);
        public Task<AgentRunLogMetadataResult> GetMetadataAsync(Guid teamId, Guid streamId, CancellationToken cancellationToken) => inner.GetMetadataAsync(teamId, streamId, cancellationToken);
        public Task<IReadOnlyList<AgentRunLogMetadata>> ListMetadataAsync(Guid teamId, Guid agentRunId, CancellationToken cancellationToken) => inner.ListMetadataAsync(teamId, agentRunId, cancellationToken);
        public Task<IReadOnlyList<AgentRunLogCaptureHead>> ListCaptureHeadsAsync(Guid teamId, Guid agentRunId, CancellationToken cancellationToken) => inner.ListCaptureHeadsAsync(teamId, agentRunId, cancellationToken);
        public Task<AgentRunLogRangeResult> ReadRangeAsync(AgentRunLogRangeRequest request, CancellationToken cancellationToken) => inner.ReadRangeAsync(request, cancellationToken);
    }

    /// <summary>
    /// Fails the owning run's every CompleteAsync outside the database, the way a provider or CAS fault would. Only that
    /// run's call fails: a wave is deployment-wide, so a neighbour's call runs through this same instance and completes.
    /// </summary>
    private sealed class ThrowingCompleteLogService(IAgentRunLogService inner, Guid agentRunId) : IAgentRunLogService
    {
        public Task<AgentRunLogOpenResult> OpenAsync(AgentRunLogOpenRequest request, CancellationToken cancellationToken) => inner.OpenAsync(request, cancellationToken);
        public Task<AgentRunLogAppendResult> AppendAsync(AgentRunLogAppendRequest request, CancellationToken cancellationToken) => inner.AppendAsync(request, cancellationToken);
        public Task<AgentRunLogFinalizeSourceResult> FinalizeSourceAsync(AgentRunLogFinalizeSourceRequest request, CancellationToken cancellationToken) => inner.FinalizeSourceAsync(request, cancellationToken);
        public Task<AgentRunLogCompleteResult> CompleteAsync(AgentRunLogCompleteRequest request, CancellationToken cancellationToken) => request.AgentRunId == agentRunId
            ? Task.FromException<AgentRunLogCompleteResult>(new InvalidOperationException($"The completion provider failed for agent run {agentRunId}."))
            : inner.CompleteAsync(request, cancellationToken);
        public Task<AgentRunLogFailCaptureResult> FailCaptureAsync(AgentRunLogFailCaptureRequest request, CancellationToken cancellationToken) => inner.FailCaptureAsync(request, cancellationToken);
        public Task<int> RecordOwnerLossAsync(AgentRunLogOwnerLossRequest request, CancellationToken cancellationToken) => inner.RecordOwnerLossAsync(request, cancellationToken);
        public Task<AgentRunLogMetadataResult> GetMetadataAsync(Guid teamId, Guid streamId, CancellationToken cancellationToken) => inner.GetMetadataAsync(teamId, streamId, cancellationToken);
        public Task<IReadOnlyList<AgentRunLogMetadata>> ListMetadataAsync(Guid teamId, Guid agentRunId, CancellationToken cancellationToken) => inner.ListMetadataAsync(teamId, agentRunId, cancellationToken);
        public Task<IReadOnlyList<AgentRunLogCaptureHead>> ListCaptureHeadsAsync(Guid teamId, Guid agentRunId, CancellationToken cancellationToken) => inner.ListCaptureHeadsAsync(teamId, agentRunId, cancellationToken);
        public Task<AgentRunLogRangeResult> ReadRangeAsync(AgentRunLogRangeRequest request, CancellationToken cancellationToken) => inner.ReadRangeAsync(request, cancellationToken);
    }

    /// <summary>
    /// Holds the owning run's first CompleteAsync until released and IGNORES its cancellation, as a provider call that does
    /// not honour its bound would, then answers it as retryable. That is how a live worker overruns its lease. Only the
    /// owning run's first call is held: a wave is deployment-wide, so a neighbour's call runs through this same instance,
    /// and holding a stranger's would stall the wave before this test's intent is claimed. Every later call passes through.
    /// </summary>
    private sealed class StallFirstOwnCompleteLogService(IAgentRunLogService inner, Guid agentRunId) : IAgentRunLogService
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _stalled;

        public Task Entered => _entered.Task;
        public void Release() => _release.TrySetResult();
        public Task<AgentRunLogOpenResult> OpenAsync(AgentRunLogOpenRequest request, CancellationToken cancellationToken) => inner.OpenAsync(request, cancellationToken);
        public Task<AgentRunLogAppendResult> AppendAsync(AgentRunLogAppendRequest request, CancellationToken cancellationToken) => inner.AppendAsync(request, cancellationToken);
        public Task<AgentRunLogFinalizeSourceResult> FinalizeSourceAsync(AgentRunLogFinalizeSourceRequest request, CancellationToken cancellationToken) => inner.FinalizeSourceAsync(request, cancellationToken);
        public async Task<AgentRunLogCompleteResult> CompleteAsync(AgentRunLogCompleteRequest request, CancellationToken cancellationToken)
        {
            if (request.AgentRunId != agentRunId || Interlocked.Exchange(ref _stalled, 1) == 1) return await inner.CompleteAsync(request, cancellationToken);

            _entered.TrySetResult();
            await _release.Task;
            return new AgentRunLogCompleteResult.Rejected(new AgentRunLogProblem(AgentRunLogProblemCode.BackendUnavailable, true));
        }
        public Task<AgentRunLogFailCaptureResult> FailCaptureAsync(AgentRunLogFailCaptureRequest request, CancellationToken cancellationToken) => inner.FailCaptureAsync(request, cancellationToken);
        public Task<int> RecordOwnerLossAsync(AgentRunLogOwnerLossRequest request, CancellationToken cancellationToken) => inner.RecordOwnerLossAsync(request, cancellationToken);
        public Task<AgentRunLogMetadataResult> GetMetadataAsync(Guid teamId, Guid streamId, CancellationToken cancellationToken) => inner.GetMetadataAsync(teamId, streamId, cancellationToken);
        public Task<IReadOnlyList<AgentRunLogMetadata>> ListMetadataAsync(Guid teamId, Guid agentRunId, CancellationToken cancellationToken) => inner.ListMetadataAsync(teamId, agentRunId, cancellationToken);
        public Task<IReadOnlyList<AgentRunLogCaptureHead>> ListCaptureHeadsAsync(Guid teamId, Guid agentRunId, CancellationToken cancellationToken) => inner.ListCaptureHeadsAsync(teamId, agentRunId, cancellationToken);
        public Task<AgentRunLogRangeResult> ReadRangeAsync(AgentRunLogRangeRequest request, CancellationToken cancellationToken) => inner.ReadRangeAsync(request, cancellationToken);
    }

    private sealed class BlockingCompleteLogService(IAgentRunLogService inner) : IAgentRunLogService
    {
        public Task<AgentRunLogOpenResult> OpenAsync(AgentRunLogOpenRequest request, CancellationToken cancellationToken) => inner.OpenAsync(request, cancellationToken);
        public Task<AgentRunLogAppendResult> AppendAsync(AgentRunLogAppendRequest request, CancellationToken cancellationToken) => inner.AppendAsync(request, cancellationToken);
        public Task<AgentRunLogFinalizeSourceResult> FinalizeSourceAsync(AgentRunLogFinalizeSourceRequest request, CancellationToken cancellationToken) => inner.FinalizeSourceAsync(request, cancellationToken);
        public async Task<AgentRunLogCompleteResult> CompleteAsync(AgentRunLogCompleteRequest request, CancellationToken cancellationToken) { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); throw new InvalidOperationException("unreachable"); }
        public Task<AgentRunLogFailCaptureResult> FailCaptureAsync(AgentRunLogFailCaptureRequest request, CancellationToken cancellationToken) => inner.FailCaptureAsync(request, cancellationToken);
        public Task<int> RecordOwnerLossAsync(AgentRunLogOwnerLossRequest request, CancellationToken cancellationToken) => inner.RecordOwnerLossAsync(request, cancellationToken);
        public Task<AgentRunLogMetadataResult> GetMetadataAsync(Guid teamId, Guid streamId, CancellationToken cancellationToken) => inner.GetMetadataAsync(teamId, streamId, cancellationToken);
        public Task<IReadOnlyList<AgentRunLogMetadata>> ListMetadataAsync(Guid teamId, Guid agentRunId, CancellationToken cancellationToken) => inner.ListMetadataAsync(teamId, agentRunId, cancellationToken);
        public Task<IReadOnlyList<AgentRunLogCaptureHead>> ListCaptureHeadsAsync(Guid teamId, Guid agentRunId, CancellationToken cancellationToken) => inner.ListCaptureHeadsAsync(teamId, agentRunId, cancellationToken);
        public Task<AgentRunLogRangeResult> ReadRangeAsync(AgentRunLogRangeRequest request, CancellationToken cancellationToken) => inner.ReadRangeAsync(request, cancellationToken);
    }

    /// <summary>
    /// Holds the first CompleteAsync of an owned run until released. Only an owned run's call opens the gate: a reconcile
    /// wave is deployment-wide, so every neighbour's call in the wave runs through this same instance, and a gate opened
    /// by a stranger holds the wave before any owned intent has been claimed.
    /// </summary>
    private sealed class GateFirstCompleteLogService(IAgentRunLogService inner, params Guid[] agentRunIds) : IAgentRunLogService
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public Task Entered => _entered.Task;
        public void Release() => _release.TrySetResult();
        public Task<AgentRunLogOpenResult> OpenAsync(AgentRunLogOpenRequest request, CancellationToken cancellationToken) => inner.OpenAsync(request, cancellationToken);
        public Task<AgentRunLogAppendResult> AppendAsync(AgentRunLogAppendRequest request, CancellationToken cancellationToken) => inner.AppendAsync(request, cancellationToken);
        public Task<AgentRunLogFinalizeSourceResult> FinalizeSourceAsync(AgentRunLogFinalizeSourceRequest request, CancellationToken cancellationToken) => inner.FinalizeSourceAsync(request, cancellationToken);
        public async Task<AgentRunLogCompleteResult> CompleteAsync(AgentRunLogCompleteRequest request, CancellationToken cancellationToken)
        {
            if (agentRunIds.Contains(request.AgentRunId) && Interlocked.Increment(ref _calls) == 1)
            {
                _entered.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }
            return await inner.CompleteAsync(request, cancellationToken);
        }
        public Task<AgentRunLogFailCaptureResult> FailCaptureAsync(AgentRunLogFailCaptureRequest request, CancellationToken cancellationToken) => inner.FailCaptureAsync(request, cancellationToken);
        public Task<int> RecordOwnerLossAsync(AgentRunLogOwnerLossRequest request, CancellationToken cancellationToken) => inner.RecordOwnerLossAsync(request, cancellationToken);
        public Task<AgentRunLogMetadataResult> GetMetadataAsync(Guid teamId, Guid streamId, CancellationToken cancellationToken) => inner.GetMetadataAsync(teamId, streamId, cancellationToken);
        public Task<IReadOnlyList<AgentRunLogMetadata>> ListMetadataAsync(Guid teamId, Guid agentRunId, CancellationToken cancellationToken) => inner.ListMetadataAsync(teamId, agentRunId, cancellationToken);
        public Task<IReadOnlyList<AgentRunLogCaptureHead>> ListCaptureHeadsAsync(Guid teamId, Guid agentRunId, CancellationToken cancellationToken) => inner.ListCaptureHeadsAsync(teamId, agentRunId, cancellationToken);
        public Task<AgentRunLogRangeResult> ReadRangeAsync(AgentRunLogRangeRequest request, CancellationToken cancellationToken) => inner.ReadRangeAsync(request, cancellationToken);
    }

    /// <summary>
    /// Pauses after the owning run's FailCaptureAsync has committed. Only that run's effect opens the gate: a reconcile
    /// wave is deployment-wide, so every neighbour's effect in the wave runs through this same instance, and a gate opened
    /// by a stranger lets the caller bump the fence before this run's effect instead of after it.
    /// </summary>
    private sealed class GateAfterFailLogService(IAgentRunLogService inner, Guid agentRunId) : IAgentRunLogService
    {
        private readonly TaskCompletionSource _effectCommitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task EffectCommitted => _effectCommitted.Task;
        public void Release() => _release.TrySetResult();
        public Task<AgentRunLogOpenResult> OpenAsync(AgentRunLogOpenRequest request, CancellationToken cancellationToken) => inner.OpenAsync(request, cancellationToken);
        public Task<AgentRunLogAppendResult> AppendAsync(AgentRunLogAppendRequest request, CancellationToken cancellationToken) => inner.AppendAsync(request, cancellationToken);
        public Task<AgentRunLogFinalizeSourceResult> FinalizeSourceAsync(AgentRunLogFinalizeSourceRequest request, CancellationToken cancellationToken) => inner.FinalizeSourceAsync(request, cancellationToken);
        public Task<AgentRunLogCompleteResult> CompleteAsync(AgentRunLogCompleteRequest request, CancellationToken cancellationToken) => inner.CompleteAsync(request, cancellationToken);
        public async Task<AgentRunLogFailCaptureResult> FailCaptureAsync(AgentRunLogFailCaptureRequest request, CancellationToken cancellationToken)
        {
            var result = await inner.FailCaptureAsync(request, cancellationToken);

            if (request.AgentRunId != agentRunId) return result;

            _effectCommitted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return result;
        }
        public Task<int> RecordOwnerLossAsync(AgentRunLogOwnerLossRequest request, CancellationToken cancellationToken) => inner.RecordOwnerLossAsync(request, cancellationToken);
        public Task<AgentRunLogMetadataResult> GetMetadataAsync(Guid teamId, Guid streamId, CancellationToken cancellationToken) => inner.GetMetadataAsync(teamId, streamId, cancellationToken);
        public Task<IReadOnlyList<AgentRunLogMetadata>> ListMetadataAsync(Guid teamId, Guid agentRunId, CancellationToken cancellationToken) => inner.ListMetadataAsync(teamId, agentRunId, cancellationToken);
        public Task<IReadOnlyList<AgentRunLogCaptureHead>> ListCaptureHeadsAsync(Guid teamId, Guid agentRunId, CancellationToken cancellationToken) => inner.ListCaptureHeadsAsync(teamId, agentRunId, cancellationToken);
        public Task<AgentRunLogRangeResult> ReadRangeAsync(AgentRunLogRangeRequest request, CancellationToken cancellationToken) => inner.ReadRangeAsync(request, cancellationToken);
    }
}
