using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.AgentRunLogging;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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
        var first = await SeedWorldAsync();
        var second = await SeedWorldAsync();
        var logs = LogService();
        var firstSession = Guid.NewGuid();
        var secondSession = Guid.NewGuid();
        var gated = new GateFirstCompleteLogService(logs);
        var recovery = Recovery(gated, new RecoveryTestOptions { MaxConcurrency = 1 });
        await recovery.DeclareAsync(Declaration(first, firstSession, 7, AgentRunLogKinds.StandardOutput), CancellationToken.None);
        await recovery.DeclareAsync(Declaration(second, secondSession, 7, AgentRunLogKinds.StandardOutput), CancellationToken.None);
        await SeedFinalizedTerminalStreamAsync(first, logs, firstSession);
        await SeedFinalizedTerminalStreamAsync(second, logs, secondSession);

        var reconcile = recovery.ReconcileAsync(CancellationToken.None);
        await gated.Entered.WaitAsync(TimeSpan.FromSeconds(2));
        using (var scope = _fixture.BeginScope())
        {
            var rows = await scope.Resolve<CodeSpaceDbContext>().AgentRunLogCaptureIntent
                .Where(value => value.AgentRunId == first.AgentRunId || value.AgentRunId == second.AgentRunId).ToListAsync();
            rows.Count(value => value.RecoveryOwnerId != null).ShouldBe(1, "later work must remain unclaimed until a worker can start it inside a fresh lease");
            rows.Count(value => value.RecoveryOwnerId == null).ShouldBe(1);
        }
        gated.Release();

        var summary = await reconcile;

        summary.Completed.ShouldBeGreaterThanOrEqualTo(2);
        using var finalScope = _fixture.BeginScope();
        var intents = await finalScope.Resolve<CodeSpaceDbContext>().AgentRunLogCaptureIntent
            .Where(value => value.AgentRunId == first.AgentRunId || value.AgentRunId == second.AgentRunId).ToListAsync();
        intents.ShouldAllBe(value => value.State == AgentRunLogCaptureIntentState.Completed && value.RecoveryAttemptCount == 1);
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
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run_log_capture_intent SET recovery_owner_id = {staleOwner}, recovery_fence_epoch = 1, recovery_attempt_count = 1, recovery_started_at = clock_timestamp(), recovery_lease_expires_at = clock_timestamp() + interval '50 milliseconds', revision = revision + 1, last_modified_at = clock_timestamp() WHERE id = {intentId}");
        }
        await Task.Delay(150);
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run_log_capture_intent SET recovery_owner_id = {currentOwner}, recovery_fence_epoch = 2, recovery_attempt_count = 2, recovery_lease_expires_at = clock_timestamp() + interval '5 seconds', revision = revision + 1, last_modified_at = clock_timestamp() WHERE id = {intentId}");
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
        var world = await SeedWorldAsync();
        var logs = LogService();
        var gated = new GateAfterFailLogService(logs);
        var recovery = Recovery(gated, new RecoveryTestOptions { TerminalGrace = TimeSpan.Zero });
        var sessionId = Guid.NewGuid();
        await recovery.DeclareAsync(Declaration(world, sessionId, 7, AgentRunLogKinds.StandardOutput), CancellationToken.None);
        var opened = (await logs.OpenAsync(Open(world, sessionId, AgentRunLogKinds.StandardOutput), CancellationToken.None)).ShouldBeOfType<AgentRunLogOpenResult.Opened>();
        await MarkTerminalAsync(world, AgentRunStatus.Succeeded, "{\"status\":\"Succeeded\"}");

        await recovery.ReconcileAsync(CancellationToken.None);
        var reconcile = recovery.ReconcileAsync(CancellationToken.None);
        await gated.EffectCommitted.WaitAsync(TimeSpan.FromSeconds(2));
        await RaiseFenceAsync(world, 8);
        gated.Release();

        var summary = await reconcile;

        summary.Superseded.ShouldBe(1);
        summary.CaptureFailed.ShouldBe(0, "the observation made under fence 7 cannot settle terminal intent state after fence 8 owns the run");
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var intent = await db.AgentRunLogCaptureIntent.SingleAsync(value => value.AgentRunId == world.AgentRunId);
        intent.State.ShouldBe(AgentRunLogCaptureIntentState.Superseded);
        intent.LastErrorCode.ShouldBe("worker-fence-changed-before-settlement");
        (await db.AgentRunLogStream.SingleAsync(value => value.Id == opened.Metadata.StreamId)).State.ShouldBe(AgentRunLogStreamState.CaptureFailed);
        var run = await db.AgentRun.AsNoTracking().SingleAsync(value => value.Id == world.AgentRunId);
        run.FenceEpoch.ShouldBe(8);
        run.Status.ShouldBe(AgentRunStatus.Succeeded);
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

    private async Task<AgentRunLogCaptureIntent> IntentAsync(World world)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().AgentRunLogCaptureIntent.AsNoTracking()
            .SingleAsync(value => value.AgentRunId == world.AgentRunId);
    }

    private AgentRunLogCaptureRecoveryService Recovery(IAgentRunLogService logs, RecoveryTestOptions? options = null)
    {
        options ??= new RecoveryTestOptions();
        using var scope = _fixture.BeginScope();
        return new AgentRunLogCaptureRecoveryService(scope.Resolve<DbContextOptions<CodeSpaceDbContext>>(), logs,
            new AgentRunLogCaptureRecoveryOptions(20, options.MaxConcurrency, TimeSpan.FromSeconds(6), options.OperationTimeout,
                new AgentRunLogCaptureRetryPolicy(options.BaseDelay, options.MaxDelay, options.MaxAttempts, options.MaxAge, options.TerminalGrace)));
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
        public Task<AgentRunLogMetadataResult> GetMetadataAsync(Guid teamId, Guid streamId, CancellationToken cancellationToken) => inner.GetMetadataAsync(teamId, streamId, cancellationToken);
        public Task<IReadOnlyList<AgentRunLogMetadata>> ListMetadataAsync(Guid teamId, Guid agentRunId, CancellationToken cancellationToken) => inner.ListMetadataAsync(teamId, agentRunId, cancellationToken);
        public Task<IReadOnlyList<AgentRunLogCaptureHead>> ListCaptureHeadsAsync(Guid teamId, Guid agentRunId, CancellationToken cancellationToken) => inner.ListCaptureHeadsAsync(teamId, agentRunId, cancellationToken);
        public Task<AgentRunLogRangeResult> ReadRangeAsync(AgentRunLogRangeRequest request, CancellationToken cancellationToken) => inner.ReadRangeAsync(request, cancellationToken);
    }

    private sealed class GateFirstCompleteLogService(IAgentRunLogService inner) : IAgentRunLogService
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
            if (Interlocked.Increment(ref _calls) == 1)
            {
                _entered.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }
            return await inner.CompleteAsync(request, cancellationToken);
        }
        public Task<AgentRunLogFailCaptureResult> FailCaptureAsync(AgentRunLogFailCaptureRequest request, CancellationToken cancellationToken) => inner.FailCaptureAsync(request, cancellationToken);
        public Task<AgentRunLogMetadataResult> GetMetadataAsync(Guid teamId, Guid streamId, CancellationToken cancellationToken) => inner.GetMetadataAsync(teamId, streamId, cancellationToken);
        public Task<IReadOnlyList<AgentRunLogMetadata>> ListMetadataAsync(Guid teamId, Guid agentRunId, CancellationToken cancellationToken) => inner.ListMetadataAsync(teamId, agentRunId, cancellationToken);
        public Task<IReadOnlyList<AgentRunLogCaptureHead>> ListCaptureHeadsAsync(Guid teamId, Guid agentRunId, CancellationToken cancellationToken) => inner.ListCaptureHeadsAsync(teamId, agentRunId, cancellationToken);
        public Task<AgentRunLogRangeResult> ReadRangeAsync(AgentRunLogRangeRequest request, CancellationToken cancellationToken) => inner.ReadRangeAsync(request, cancellationToken);
    }

    private sealed class GateAfterFailLogService(IAgentRunLogService inner) : IAgentRunLogService
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
            _effectCommitted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return result;
        }
        public Task<AgentRunLogMetadataResult> GetMetadataAsync(Guid teamId, Guid streamId, CancellationToken cancellationToken) => inner.GetMetadataAsync(teamId, streamId, cancellationToken);
        public Task<IReadOnlyList<AgentRunLogMetadata>> ListMetadataAsync(Guid teamId, Guid agentRunId, CancellationToken cancellationToken) => inner.ListMetadataAsync(teamId, agentRunId, cancellationToken);
        public Task<IReadOnlyList<AgentRunLogCaptureHead>> ListCaptureHeadsAsync(Guid teamId, Guid agentRunId, CancellationToken cancellationToken) => inner.ListCaptureHeadsAsync(teamId, agentRunId, cancellationToken);
        public Task<AgentRunLogRangeResult> ReadRangeAsync(AgentRunLogRangeRequest request, CancellationToken cancellationToken) => inner.ReadRangeAsync(request, cancellationToken);
    }
}
