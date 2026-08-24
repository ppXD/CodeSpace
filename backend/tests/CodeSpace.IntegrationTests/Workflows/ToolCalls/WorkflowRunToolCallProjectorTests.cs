using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Workflows.ToolCalls;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.ToolCalls;

/// <summary>
/// Real-Postgres pins for the observation-only governed tool-call projection. The source ledger remains the sole
/// approval, exactly-once, execution and replay authority; these tests prove the 0141 rows are bounded shadows.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class WorkflowRunToolCallProjectorTests
{
    private const string InputHash = "0000000000000000000000000000000000000000000000000000000000000000";
    private readonly PostgresFixture _fixture;

    public WorkflowRunToolCallProjectorTests(PostgresFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData(ToolCallLedgerStatus.Succeeded, ToolCallAttemptStatus.Succeeded, ToolCallState.Completed, null)]
    [InlineData(ToolCallLedgerStatus.Failed, ToolCallAttemptStatus.Indeterminate, ToolCallState.Abandoned, WorkflowRunToolCallProjector.FailedOutcomeUnknown)]
    [InlineData(ToolCallLedgerStatus.Denied, ToolCallAttemptStatus.Denied, ToolCallState.Completed, WorkflowRunToolCallProjector.GovernanceDenied)]
    [InlineData(ToolCallLedgerStatus.Expired, ToolCallAttemptStatus.Denied, ToolCallState.Completed, WorkflowRunToolCallProjector.ApprovalExpired)]
    public async Task Terminal_source_maps_conservatively_without_copying_payloads(ToolCallLedgerStatus sourceStatus,
        ToolCallAttemptStatus attemptStatus, ToolCallState callState, string? errorCode)
    {
        var world = await SeedWorldAsync(AgentRunStatus.Running);
        var marker = $"secret-{Guid.NewGuid():N}";
        var ledger = await SeedLedgerAsync(world, sourceStatus, "git.open_pr", new LedgerOptions
        {
            ResultJson = $$"""{"summary":"{{marker}}"}""", Error = marker,
        });

        (await SweepAsync(250)).CallsProjected.ShouldBeGreaterThanOrEqualTo(1);

        var observation = await ReadProjectionAsync(ledger.Id);
        observation.Call.SourceKind.ShouldBe(WorkflowRunToolCallProjector.SourceKind);
        observation.Call.SourceCorrelationId.ShouldBe(ledger.Id);
        observation.Call.WorkflowRunId.ShouldBe(world.WorkflowRunId);
        observation.Call.TeamId.ShouldBe(world.TeamId);
        observation.Call.NodeId.ShouldBe(world.NodeId);
        observation.Call.IterationKey.ShouldBe(world.IterationKey);
        observation.Call.CallOrdinal.ShouldBe(ledger.AdmissionOrdinal!.Value);
        observation.Call.Purpose.ShouldBe(WorkflowRunToolCallProjector.Purpose);
        observation.Call.ToolKind.ShouldBe(WorkflowRunToolCallProjector.AdapterToolKind);
        observation.Call.ToolName.ShouldBe("git.open_pr");
        observation.Call.ToolNamespace.ShouldBeNull();
        observation.Call.EffectClass.ShouldBe(ToolCallEffectClass.SideEffecting);
        observation.Call.ExecutionAttemptId.ShouldBeNull("the source has no authorization ordinal/generation, so the execution identity triple stays honestly absent");
        observation.Call.ArgumentsRedaction.ShouldBe(NativeRecordRedaction.Withheld);
        observation.Call.CaptureCompleteness.ShouldBe(WorkflowRunCaptureCompleteness.Unavailable);
        observation.Call.ArgumentsArtifactId.ShouldBeNull();
        observation.Call.ArgumentsDigest.ShouldBeNull();
        observation.Call.RedactionPolicy.ShouldBeNull();
        observation.Call.State.ShouldBe(callState);
        observation.Call.ErrorCode.ShouldBe(callState == ToolCallState.Abandoned ? errorCode : null);

        observation.Attempt.Status.ShouldBe(attemptStatus);
        observation.Attempt.ErrorCode.ShouldBe(errorCode);
        observation.Attempt.ResultRedaction.ShouldBe(NativeRecordRedaction.Withheld);
        observation.Attempt.CaptureCompleteness.ShouldBe(WorkflowRunCaptureCompleteness.Unavailable);
        observation.Attempt.ResultArtifactId.ShouldBeNull();
        observation.Attempt.ErrorArtifactId.ShouldBeNull();
        observation.Attempt.ResultDigest.ShouldBeNull();
        observation.Attempt.ErrorDigest.ShouldBeNull();
        observation.Attempt.RedactionPolicy.ShouldBeNull();
        observation.Attempt.ErrorMessage.ShouldBeNull();
        observation.Attempt.TransportKind.ShouldBeNull();
        observation.Attempt.InvocationId.ShouldBeNull();
        observation.Call.ErrorMessage.ShouldBeNull();
        (observation.Call.ErrorMessage ?? string.Empty).ShouldNotContain(marker);
        (observation.Attempt.ErrorMessage ?? string.Empty).ShouldNotContain(marker);
    }

    [Fact]
    public async Task Terminal_source_time_is_not_stretched_to_a_later_agent_run_completion()
    {
        var world = await SeedWorldAsync(AgentRunStatus.Succeeded);
        // Whole-second instants: Postgres timestamptz keeps microseconds, so UtcNow's 100ns tick residue is
        // truncated on the round-trip and an exact ShouldBe reds intermittently. Second-aligned times survive
        // the round-trip losslessly while staying now-relative (the sweep windows on recency).
        var admittedAt = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds());
        var sourceTerminalAt = admittedAt.AddMinutes(1);
        var ledger = await SeedLedgerAsync(world, ToolCallLedgerStatus.Succeeded, "git.open_pr", new LedgerOptions
        {
            CreatedAt = admittedAt, LastModifiedAt = sourceTerminalAt,
        });

        await SweepAsync(250);

        var observation = await ReadProjectionAsync(ledger.Id);
        observation.Attempt.StartedAt.ShouldBe(admittedAt, "ledger admission is the adapter's durable lower bound, not an exact provider-dispatch latency claim");
        observation.Attempt.CompletedAt.ShouldBe(sourceTerminalAt);
        observation.Call.TerminalAt.ShouldBe(sourceTerminalAt);
    }

    [Fact]
    public async Task Terminal_run_with_a_live_worker_lease_does_not_freeze_a_running_call_before_its_success_CAS()
    {
        var world = await SeedWorldAsync(AgentRunStatus.Running);
        var ledger = await SeedLedgerAsync(world, ToolCallLedgerStatus.Running, "git.merge_pr");

        await TerminalizeAgentAsync(world.AgentRunId, AgentRunStatus.Cancelled, DateTimeOffset.UtcNow.AddMinutes(10));
        await SweepAsync(250);
        (await CountProjectionsAsync(ledger.Id)).ShouldBe(0);

        await UpdateLedgerStatusAsync(ledger.Id, ToolCallLedgerStatus.Succeeded);
        await SweepAsync(250);

        var observation = await ReadProjectionAsync(ledger.Id);
        observation.Attempt.Status.ShouldBe(ToolCallAttemptStatus.Succeeded);
        observation.Call.State.ShouldBe(ToolCallState.Completed);
    }

    [Fact]
    public async Task Awaiting_approval_never_infers_denial_from_agent_terminal_status_and_waits_for_authoritative_expiry()
    {
        var world = await SeedWorldAsync(AgentRunStatus.Running);
        var ledger = await SeedLedgerAsync(world, ToolCallLedgerStatus.AwaitingApproval, "git.merge_pr");

        await TerminalizeAgentAsync(world.AgentRunId, AgentRunStatus.NeedsReview);
        await SweepAsync(250);
        (await CountProjectionsAsync(ledger.Id)).ShouldBe(0);

        await UpdateLedgerStatusAsync(ledger.Id, ToolCallLedgerStatus.Expired);
        await SweepAsync(250);

        var observation = await ReadProjectionAsync(ledger.Id);
        observation.Attempt.Status.ShouldBe(ToolCallAttemptStatus.Denied);
        observation.Attempt.ErrorCode.ShouldBe(WorkflowRunToolCallProjector.ApprovalExpired);
        observation.Call.State.ShouldBe(ToolCallState.Completed);
    }

    [Fact]
    public async Task Stale_reaper_terminal_CAS_is_projected_as_indeterminate_instead_of_a_synthetic_agent_status_inference()
    {
        var world = await SeedWorldAsync(AgentRunStatus.Running);
        var ledger = await SeedLedgerAsync(world, ToolCallLedgerStatus.Pending, "git.open_pr");
        var now = DateTimeOffset.UtcNow;
        await TerminalizeAgentAsync(world.AgentRunId, AgentRunStatus.Failed, now.AddMinutes(-1));

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IToolCallLedgerService>().ExpireStaleToolCallsAsync(now, CancellationToken.None)).ShouldBe(1);
        (await ReadLedgerStatusAsync(ledger.Id)).ShouldBe(ToolCallLedgerStatus.Failed);

        await SweepAsync(250);

        var observation = await ReadProjectionAsync(ledger.Id);
        observation.Attempt.Status.ShouldBe(ToolCallAttemptStatus.Indeterminate);
        observation.Attempt.ErrorCode.ShouldBe(WorkflowRunToolCallProjector.FailedOutcomeUnknown);
        observation.Call.State.ShouldBe(ToolCallState.Abandoned);
    }

    [Fact]
    public async Task Exact_source_admission_ordinal_is_preserved_and_excluded_decisions_leave_visible_gaps()
    {
        var world = await SeedWorldAsync(AgentRunStatus.Running);
        var decision = await SeedLedgerAsync(world, ToolCallLedgerStatus.Succeeded, DecisionToolKinds.DecisionRequest);
        var first = await SeedLedgerAsync(world, ToolCallLedgerStatus.Succeeded, "git.open_pr");
        await SeedLedgerAsync(world, ToolCallLedgerStatus.Succeeded, DecisionToolKinds.DecisionRequest);
        var second = await SeedLedgerAsync(world, ToolCallLedgerStatus.Succeeded, "git.merge_pr");

        await SweepAsync(250);

        (await CountProjectionsAsync(decision.Id)).ShouldBe(0);
        var firstCall = (await ReadProjectionAsync(first.Id)).Call;
        var secondCall = (await ReadProjectionAsync(second.Id)).Call;
        firstCall.CallOrdinal.ShouldBe(first.AdmissionOrdinal!.Value);
        secondCall.CallOrdinal.ShouldBe(second.AdmissionOrdinal!.Value);
        firstCall.CallOrdinal.ShouldBe(2);
        secondCall.CallOrdinal.ShouldBe(4);
    }

    [Fact]
    public async Task Standalone_legacy_and_corrupt_scope_sources_never_publish_and_are_bounded_diagnostics_only()
    {
        var valid = await SeedWorldAsync(AgentRunStatus.Succeeded);
        var standalone = await SeedWorldAsync(AgentRunStatus.Succeeded, standalone: true);
        var foreign = await SeedForeignWorkflowWorldAsync();
        var missingAgent = new RunWorld(valid.TeamId, valid.WorkflowRunId, Guid.Parse("ffffffff-ffff-ffff-ffff-fffffffffff1"), valid.NodeId, valid.IterationKey);
        var standaloneLedger = await SeedLedgerAsync(standalone, ToolCallLedgerStatus.Succeeded, "git.open_pr", new LedgerOptions { Id = Guid.Parse("ffffffff-ffff-ffff-ffff-fffffffffff2") });
        var foreignLedger = await SeedLedgerAsync(foreign, ToolCallLedgerStatus.Succeeded, "git.merge_pr", new LedgerOptions { Id = Guid.Parse("ffffffff-ffff-ffff-ffff-fffffffffff3") });
        var missingLedger = await SeedLedgerAsync(missingAgent, ToolCallLedgerStatus.Succeeded, "git.create_issue", new LedgerOptions { Id = Guid.Parse("ffffffff-ffff-ffff-ffff-fffffffffff4") });
        var legacyLedgerId = await SeedLegacyLedgerAsync(valid, Guid.Parse("ffffffff-ffff-ffff-ffff-fffffffffff5"));

        var result = await SweepAsync(1000);

        (await CountProjectionsAsync(standaloneLedger.Id)).ShouldBe(0);
        (await CountProjectionsAsync(foreignLedger.Id)).ShouldBe(0);
        (await CountProjectionsAsync(missingLedger.Id)).ShouldBe(0);
        (await CountProjectionsAsync(legacyLedgerId)).ShouldBe(0);
        result.DiagnosticRowsObserved.ShouldBeLessThanOrEqualTo(1000);
        result.StandaloneSourcesObserved.ShouldBeGreaterThanOrEqualTo(1);
        result.InvalidScopeSourcesObserved.ShouldBeGreaterThanOrEqualTo(2);
        result.LegacyUnorderedSourcesObserved.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Bounded_antijoin_is_idempotent_and_overlapping_sweeps_admit_one_projection()
    {
        var world = await SeedWorldAsync(AgentRunStatus.Running);
        var ledger = await SeedLedgerAsync(world, ToolCallLedgerStatus.Succeeded, "git.open_pr");

        var results = await Task.WhenAll(Task.Run(() => SweepAsync(1)), Task.Run(() => SweepAsync(1)));

        results.Sum(value => value.CallsProjected).ShouldBe(1);
        (await SweepAsync(1)).CallsProjected.ShouldBe(0);
        var observation = await ReadProjectionAsync(ledger.Id);
        observation.Call.AttemptCount.ShouldBe(1);
        observation.Attempt.AttemptOrdinal.ShouldBe(1);
    }

    [Fact]
    public async Task Candidate_query_uses_the_global_partial_keyset_index_without_seqscan_or_sort()
    {
        await SeedCandidateFloodAsync(10_050);
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var settings = new NpgsqlCommand("SET LOCAL enable_seqscan = off", connection, transaction))
            await settings.ExecuteNonQueryAsync();
        await using var command = new NpgsqlCommand("EXPLAIN (COSTS OFF) " + WorkflowRunToolCallProjector.CandidateSql, connection, transaction);
        command.Parameters.AddWithValue("batch_size", 250);
        var plan = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) plan.Add(reader.GetString(0));
        var text = string.Join('\n', plan);

        text.ShouldContain("ix_tool_call_ledger_projection_candidate");
        text.ShouldNotContain("Seq Scan on tool_call_ledger");
        text.ShouldNotContain("Sort");
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Invalid_batch_size_fails_before_querying()
    {
        using var scope = _fixture.BeginScope();
        var projector = scope.Resolve<IWorkflowRunToolCallProjector>();
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => projector.SweepAsync(0, CancellationToken.None));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => projector.SweepAsync(1001, CancellationToken.None));
    }

    private async Task<WorkflowRunToolCallProjectionResult> SweepAsync(int batchSize)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IWorkflowRunToolCallProjector>().SweepAsync(batchSize, CancellationToken.None);
    }

    private async Task<Projection> ReadProjectionAsync(Guid sourceId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var call = await db.WorkflowRunToolCall.AsNoTracking().SingleAsync(value => value.SourceKind == WorkflowRunToolCallProjector.SourceKind && value.SourceCorrelationId == sourceId);
        var attempt = await db.WorkflowRunToolCallAttempt.AsNoTracking().SingleAsync(value => value.ToolCallId == call.Id);
        return new Projection(call, attempt);
    }

    private async Task<int> CountProjectionsAsync(Guid sourceId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunToolCall.CountAsync(value => value.SourceKind == WorkflowRunToolCallProjector.SourceKind && value.SourceCorrelationId == sourceId);
    }

    private async Task<RunWorld> SeedWorldAsync(AgentRunStatus agentStatus, bool standalone = false)
    {
        await DrainExistingBacklogAsync();
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        Guid workflowId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            workflowId = await scope.Resolve<MediatR.IMediator>().Send(new CreateWorkflowCommand
            {
                Name = "tool-call-projector-" + Guid.NewGuid().ToString("N")[..8], Definition = WorkflowsTestSeed.MinimalDefinition(),
                Activations = new List<WorkflowActivationInput>(), Enabled = true,
            });
        var workflowRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        var agentRunId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.AgentRun.Add(new AgentRun
            {
                Id = agentRunId, TeamId = teamId, WorkflowRunId = standalone ? null : workflowRunId, NodeId = "agent",
                IterationKey = "agent#turn1", Harness = "codex-cli", Status = agentStatus, TaskJson = "{}",
                StartedAt = now.AddSeconds(-5), CompletedAt = IsTerminal(agentStatus) ? now : null,
                CreatedDate = now.AddSeconds(-5), CreatedBy = SystemUsers.SeederId, LastModifiedDate = now, LastModifiedBy = SystemUsers.SeederId,
            });
            await db.SaveChangesAsync();
        }
        return new RunWorld(teamId, workflowRunId, agentRunId, "agent", "agent#turn1");
    }

    private async Task DrainExistingBacklogAsync()
    {
        for (var iteration = 0; iteration < 100; iteration++)
            if ((await SweepAsync(1000)).CallsProjected == 0) return;
        throw new InvalidOperationException("The governed tool-call projection backlog did not drain within 100 bounded sweeps.");
    }

    private async Task<RunWorld> SeedForeignWorkflowWorldAsync()
    {
        var owner = await SeedWorldAsync(AgentRunStatus.Succeeded);
        var foreign = await SeedWorldAsync(AgentRunStatus.Succeeded);
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var agent = await db.AgentRun.SingleAsync(value => value.Id == owner.AgentRunId);
        agent.WorkflowRunId = foreign.WorkflowRunId;
        await db.SaveChangesAsync();
        return owner;
    }

    private async Task<ToolCallLedger> SeedLedgerAsync(RunWorld world, ToolCallLedgerStatus status, string toolKind, LedgerOptions? options = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = options?.CreatedAt ?? DateTimeOffset.UtcNow;
        var row = new ToolCallLedger
        {
            Id = options?.Id ?? Guid.NewGuid(), TeamId = world.TeamId, AgentRunId = world.AgentRunId, ToolKind = toolKind,
            IdempotencyKey = $"{toolKind}:{Guid.NewGuid():N}", InputHash = InputHash, Status = status,
            ResultJson = options?.ResultJson, Error = options?.Error, CreatedDate = now, LastModifiedDate = options?.LastModifiedAt ?? now.AddMilliseconds(1),
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        };
        db.ToolCallLedger.Add(row);
        await db.SaveChangesAsync();
        return row;
    }

    private async Task<Guid> SeedLegacyLedgerAsync(RunWorld world, Guid ledgerId)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await using (var disable = new NpgsqlCommand("ALTER TABLE tool_call_ledger DISABLE TRIGGER trg_tool_call_ledger_assign_admission_ordinal", connection, transaction))
                await disable.ExecuteNonQueryAsync();
            await using (var insert = new NpgsqlCommand("""
                INSERT INTO tool_call_ledger (id, team_id, agent_run_id, admission_ordinal, tool_kind, idempotency_key,
                    input_hash, status, fence_epoch, created_by, last_modified_by)
                VALUES (@id, @team, @agent, NULL, 'git.open_pr', @key, @hash, 'Succeeded', 0, @actor, @actor)
                """, connection, transaction))
            {
                insert.Parameters.AddWithValue("id", ledgerId);
                insert.Parameters.AddWithValue("team", world.TeamId);
                insert.Parameters.AddWithValue("agent", world.AgentRunId);
                insert.Parameters.AddWithValue("key", $"legacy:{ledgerId:N}");
                insert.Parameters.AddWithValue("hash", InputHash);
                insert.Parameters.AddWithValue("actor", SystemUsers.SeederId);
                await insert.ExecuteNonQueryAsync();
            }
            await using (var enable = new NpgsqlCommand("ALTER TABLE tool_call_ledger ENABLE TRIGGER trg_tool_call_ledger_assign_admission_ordinal", connection, transaction))
                await enable.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
        return ledgerId;
    }

    private async Task SeedCandidateFloodAsync(int count)
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await using (var disable = new NpgsqlCommand("ALTER TABLE tool_call_ledger DISABLE TRIGGER trg_tool_call_ledger_assign_admission_ordinal", connection, transaction))
                await disable.ExecuteNonQueryAsync();
            await using (var insert = new NpgsqlCommand("""
                INSERT INTO tool_call_ledger (id, team_id, agent_run_id, admission_ordinal, tool_kind, idempotency_key,
                    input_hash, status, fence_epoch, created_date, created_by, last_modified_date, last_modified_by)
                SELECT md5(@salt || value::text)::uuid, @team, @missing_agent, value, 'git.open_pr',
                    'plan-flood:' || @salt || ':' || value::text, @hash, 'Succeeded', 0,
                    clock_timestamp() - interval '1 day' + value * interval '1 microsecond', @actor,
                    clock_timestamp() - interval '1 day' + value * interval '1 microsecond', @actor
                FROM generate_series(1, @count) AS value
                """, connection, transaction))
            {
                insert.Parameters.AddWithValue("salt", Guid.NewGuid().ToString("N"));
                insert.Parameters.AddWithValue("team", teamId);
                insert.Parameters.AddWithValue("missing_agent", Guid.NewGuid());
                insert.Parameters.AddWithValue("count", count);
                insert.Parameters.AddWithValue("hash", InputHash);
                insert.Parameters.AddWithValue("actor", SystemUsers.SeederId);
                await insert.ExecuteNonQueryAsync();
            }
            await using (var enable = new NpgsqlCommand("ALTER TABLE tool_call_ledger ENABLE TRIGGER trg_tool_call_ledger_assign_admission_ordinal", connection, transaction))
                await enable.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
            await using var analyze = new NpgsqlCommand("ANALYZE tool_call_ledger, agent_run, workflow_run, workflow_run_tool_call", connection);
            await analyze.ExecuteNonQueryAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private async Task TerminalizeAgentAsync(Guid agentRunId, AgentRunStatus status, DateTimeOffset? leaseExpiresAt = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var run = await db.AgentRun.SingleAsync(value => value.Id == agentRunId);
        run.Status = status;
        run.CompletedAt = DateTimeOffset.UtcNow;
        run.LeaseExpiresAt = leaseExpiresAt;
        run.LastModifiedDate = run.CompletedAt.Value;
        await db.SaveChangesAsync();
    }

    private async Task UpdateLedgerStatusAsync(Guid ledgerId, ToolCallLedgerStatus status)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var ledger = await db.ToolCallLedger.SingleAsync(value => value.Id == ledgerId);
        ledger.Status = status;
        ledger.LastModifiedDate = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private async Task<ToolCallLedgerStatus> ReadLedgerStatusAsync(Guid ledgerId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().ToolCallLedger.Where(value => value.Id == ledgerId).Select(value => value.Status).SingleAsync();
    }

    private static bool IsTerminal(AgentRunStatus status) => status is not AgentRunStatus.Queued and not AgentRunStatus.Running;

    private sealed record RunWorld(Guid TeamId, Guid WorkflowRunId, Guid AgentRunId, string NodeId, string IterationKey);
    private sealed record Projection(WorkflowRunToolCall Call, WorkflowRunToolCallAttempt Attempt);
    private sealed class LedgerOptions
    {
        public Guid? Id { get; init; }
        public string? ResultJson { get; init; }
        public string? Error { get; init; }
        public DateTimeOffset? CreatedAt { get; init; }
        public DateTimeOffset? LastModifiedAt { get; init; }
    }
}
