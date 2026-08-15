using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Contracts;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Persistence;

/// <summary>
/// Real-Postgres pins for migration 0124. These assertions deliberately cover the database constraints as well as
/// EF round-trip: a model-call identity or usage counter written outside EF must not be able to bypass the contract.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class WorkflowRunModelCallSchemaFlowTests
{
    private readonly PostgresFixture _fixture;

    public WorkflowRunModelCallSchemaFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task The_two_tables_columns_and_hot_path_indexes_are_exact()
    {
        (await ColumnsAsync("workflow_run_model_call")).ShouldBe(new[]
        {
            "call_ordinal", "capture_completeness", "capture_source", "created_by", "created_date", "execution_attempt_id",
            "execution_attempt_ordinal", "execution_generation", "id", "iteration_key", "last_modified_by", "last_modified_date",
            "node_id", "plan_version", "purpose", "request_artifact_id", "requested_model", "requested_model_row_id", "requested_provider",
            "schema_version", "selection_policy", "source_correlation_id", "source_kind", "team_id", "workflow_run_id", "work_plan_id",
            "work_unit_contract_hash", "work_unit_id",
        }.Order());
        (await ColumnsAsync("workflow_run_model_call_attempt")).ShouldBe(new[]
        {
            "attempt_ordinal", "cache_read_tokens", "cache_write_tokens", "capture_completeness", "capture_source", "completed_at",
            "cost_amount", "cost_currency", "created_by", "created_date", "effective_model", "effective_model_row_id", "effective_provider",
            "endpoint_fingerprint", "error_artifact_id", "error_code", "finish_reason", "first_token_at", "http_status_code", "id",
            "input_tokens", "last_modified_by", "last_modified_date", "model_call_id", "output_tokens", "pricing_version",
            "provider_request_id", "reasoning_tokens", "request_artifact_id", "response_artifact_id", "schema_version", "started_at",
            "status", "source_evidence_revision", "source_started_record_id", "source_terminal_record_id", "team_id", "transport_kind", "workflow_run_id",
        }.Order());

        var indexes = await IndexesAsync();
        indexes.ShouldContain("ix_workflow_run_model_call_run_created");
        indexes.ShouldContain("ix_workflow_run_model_call_execution_attempt");
        indexes.ShouldContain("ix_workflow_run_model_call_work_unit");
        indexes.ShouldContain("ix_workflow_run_model_call_requested_model_row");
        indexes.ShouldContain("ux_workflow_run_model_call_attempt_ordinal");
        indexes.ShouldContain("ix_workflow_run_model_call_attempt_run_started");
        indexes.ShouldContain("ix_workflow_run_model_call_attempt_effective_model_row");
        indexes.ShouldContain("ux_workflow_run_model_call_source_identity");
        indexes.ShouldContain("ux_workflow_run_model_call_attempt_source_terminal");
        indexes.ShouldContain("ix_workflow_run_model_call_attempt_late_start");
    }

    [Fact]
    public async Task Logical_call_and_physical_attempt_round_trip_without_collapsing_requested_and_effective_models()
    {
        var (runId, teamId) = await SeedRunAsync();
        var callId = Guid.NewGuid();
        var requestArtifactId = Guid.NewGuid();
        var providerRequestArtifactId = Guid.NewGuid();
        var responseArtifactId = Guid.NewGuid();
        var errorArtifactId = Guid.NewGuid();
        var requestedModelRowId = Guid.NewGuid();
        var effectiveModelRowId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow.AddSeconds(-3);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.WorkflowRunModelCall.Add(new WorkflowRunModelCall
            {
                Id = callId,
                TeamId = teamId,
                WorkflowRunId = runId,
                NodeId = "supervisor",
                IterationKey = "supervisor#turn2",
                WorkPlanId = Guid.NewGuid(),
                PlanVersion = 4,
                WorkUnitId = "implement-storage",
                WorkUnitContractHash = "sha256:contract",
                ExecutionAttemptId = Guid.NewGuid(),
                ExecutionAttemptOrdinal = 2,
                ExecutionGeneration = 7,
                CallOrdinal = 3,
                Purpose = "supervisor.decision/v1",
                RequestedProvider = "auto",
                RequestedModel = "frontier-policy",
                RequestedModelRowId = requestedModelRowId,
                SelectionPolicy = "auto-capability-tier/v1",
                RequestArtifactId = requestArtifactId,
                CaptureSource = "in-process",
                CaptureCompleteness = WorkflowRunCaptureCompleteness.Exact,
                SchemaVersion = WorkflowRunDataContract.CurrentVersion,
            });
            db.WorkflowRunModelCallAttempt.Add(new WorkflowRunModelCallAttempt
            {
                Id = Guid.NewGuid(),
                TeamId = teamId,
                WorkflowRunId = runId,
                ModelCallId = callId,
                AttemptOrdinal = 2,
                EffectiveProvider = "anthropic",
                EffectiveModel = "claude-sonnet-4-5",
                EffectiveModelRowId = effectiveModelRowId,
                TransportKind = "in-process/v1",
                EndpointFingerprint = "sha256:endpoint-without-secrets",
                ProviderRequestId = "req_123",
                RequestArtifactId = providerRequestArtifactId,
                ResponseArtifactId = responseArtifactId,
                ErrorArtifactId = errorArtifactId,
                Status = "Failed",
                FinishReason = "rate_limit",
                HttpStatusCode = 429,
                CaptureSource = "provider-hook",
                CaptureCompleteness = WorkflowRunCaptureCompleteness.Exact,
                InputTokens = 50_001,
                OutputTokens = 8_192,
                CacheReadTokens = 40_000,
                CacheWriteTokens = 1_024,
                ReasoningTokens = 3_000,
                CostAmount = 1.23456789m,
                CostCurrency = "USD",
                PricingVersion = "anthropic-2026-08-01",
                StartedAt = startedAt,
                FirstTokenAt = startedAt.AddMilliseconds(850),
                CompletedAt = startedAt.AddSeconds(3),
                SchemaVersion = WorkflowRunDataContract.CurrentVersion,
            });
            await db.SaveChangesAsync();
        }

        using var verify = _fixture.BeginScope();
        var read = verify.Resolve<CodeSpaceDbContext>();
        var call = await read.WorkflowRunModelCall.AsNoTracking().SingleAsync(c => c.Id == callId);
        var attempt = await read.WorkflowRunModelCallAttempt.AsNoTracking().SingleAsync(a => a.ModelCallId == callId);

        call.RequestedModel.ShouldBe("frontier-policy");
        call.Purpose.ShouldBe("supervisor.decision/v1");
        call.RequestedModelRowId.ShouldBe(requestedModelRowId);
        call.SelectionPolicy.ShouldBe("auto-capability-tier/v1");
        call.ExecutionGeneration.ShouldBe(7);
        call.RequestArtifactId.ShouldBe(requestArtifactId);
        attempt.EffectiveModel.ShouldBe("claude-sonnet-4-5");
        attempt.EffectiveModelRowId.ShouldBe(effectiveModelRowId);
        attempt.TransportKind.ShouldBe("in-process/v1");
        attempt.EndpointFingerprint.ShouldBe("sha256:endpoint-without-secrets");
        attempt.FinishReason.ShouldBe("rate_limit");
        attempt.HttpStatusCode.ShouldBe(429);
        attempt.ErrorArtifactId.ShouldBe(errorArtifactId);
        attempt.RequestArtifactId.ShouldBe(providerRequestArtifactId);
        attempt.ResponseArtifactId.ShouldBe(responseArtifactId);
        attempt.InputTokens.ShouldBe(50_001);
        attempt.CacheReadTokens.ShouldBe(40_000);
        attempt.ReasoningTokens.ShouldBe(3_000);
        attempt.CostAmount.ShouldBe(1.23456789m);
        attempt.FirstTokenAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Database_rejects_partial_identity_negative_usage_and_inverted_timing()
    {
        var (runId, teamId) = await SeedRunAsync();
        var invalidCall = Call(teamId, runId);
        invalidCall.WorkPlanId = Guid.NewGuid();

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.WorkflowRunModelCall.Add(invalidCall);
            (await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>()).InnerException.ShouldNotBeNull();
        }

        var invalidGenerationCall = Call(teamId, runId);
        invalidGenerationCall.ExecutionAttemptId = Guid.NewGuid();
        invalidGenerationCall.ExecutionAttemptOrdinal = 1;
        invalidGenerationCall.ExecutionGeneration = 0;
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.WorkflowRunModelCall.Add(invalidGenerationCall);
            await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
        }

        var validCall = Call(teamId, runId);
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.WorkflowRunModelCall.Add(validCall);
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var now = DateTimeOffset.UtcNow;
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.WorkflowRunModelCallAttempt.Add(new WorkflowRunModelCallAttempt
            {
                Id = Guid.NewGuid(), TeamId = teamId, WorkflowRunId = validCall.WorkflowRunId, ModelCallId = validCall.Id,
                AttemptOrdinal = 1, Status = "Failed", CaptureSource = "harness-native", CaptureCompleteness = WorkflowRunCaptureCompleteness.Partial,
                InputTokens = -1, StartedAt = now, FirstTokenAt = now.AddSeconds(-1), SchemaVersion = WorkflowRunDataContract.CurrentVersion,
            });
            await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.WorkflowRunModelCallAttempt.Add(new WorkflowRunModelCallAttempt
            {
                Id = Guid.NewGuid(), TeamId = teamId, WorkflowRunId = validCall.WorkflowRunId, ModelCallId = validCall.Id,
                AttemptOrdinal = 2, Status = "Failed", HttpStatusCode = 99, CaptureSource = "harness-native",
                CaptureCompleteness = WorkflowRunCaptureCompleteness.Partial, StartedAt = DateTimeOffset.UtcNow,
                SchemaVersion = WorkflowRunDataContract.CurrentVersion,
            });
            await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.WorkflowRunModelCallAttempt.Add(new WorkflowRunModelCallAttempt
            {
                Id = Guid.NewGuid(), TeamId = teamId, WorkflowRunId = validCall.WorkflowRunId, ModelCallId = validCall.Id,
                AttemptOrdinal = 3, Status = "Failed", CostAmount = 0.01m, CaptureSource = "harness-native",
                CaptureCompleteness = WorkflowRunCaptureCompleteness.Partial, StartedAt = DateTimeOffset.UtcNow,
                SchemaVersion = WorkflowRunDataContract.CurrentVersion,
            });
            await db.SaveChangesAsync().ShouldThrowAsync<DbUpdateException>();
        }
    }

    private static WorkflowRunModelCall Call(Guid teamId, Guid runId) => new()
    {
        Id = Guid.NewGuid(), TeamId = teamId, WorkflowRunId = runId, IterationKey = "", CallOrdinal = 1, Purpose = "test/v1",
        CaptureSource = "harness-native", CaptureCompleteness = WorkflowRunCaptureCompleteness.Partial, SchemaVersion = WorkflowRunDataContract.CurrentVersion,
    };

    private async Task<(Guid RunId, Guid TeamId)> SeedRunAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        Guid workflowId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            workflowId = await scope.Resolve<MediatR.IMediator>().Send(new CreateWorkflowCommand
            {
                Name = "model-call-schema-" + Guid.NewGuid().ToString("N")[..8],
                Definition = WorkflowsTestSeed.MinimalDefinition(),
                Activations = new List<WorkflowActivationInput>(),
                Enabled = true,
            });
        }

        return (await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId), teamId);
    }

    private async Task<IReadOnlyList<string>> ColumnsAsync(string table)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @table ORDER BY column_name", connection);
        command.Parameters.AddWithValue("table", table);
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        return names;
    }

    private async Task<IReadOnlyList<string>> IndexesAsync()
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT indexname FROM pg_indexes WHERE schemaname = 'public' AND tablename IN ('workflow_run_model_call', 'workflow_run_model_call_attempt')", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        return names;
    }
}
