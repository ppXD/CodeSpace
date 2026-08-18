using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public sealed class WorkflowRunToolCallConfiguration : IEntityTypeConfiguration<WorkflowRunToolCall>
{
    public void Configure(EntityTypeBuilder<WorkflowRunToolCall> builder)
    {
        builder.ToTable(WorkflowRunDataNames.ToolCall, table =>
        {
            table.HasCheckConstraint("ck_workflow_run_tool_call_capture_completeness", "capture_completeness IN ('Exact', 'RedactedExact', 'Partial', 'Unavailable', 'Corrupt', 'LegacyUnknown')");
            table.HasCheckConstraint("ck_workflow_run_tool_call_effect_class", "effect_class IN ('ReadOnly', 'SideEffecting', 'Unknown')");
            table.HasCheckConstraint("ck_workflow_run_tool_call_error", "(error_code IS NULL AND error_message IS NULL) OR (error_code IS NOT NULL AND btrim(error_code) <> '')");
            table.HasCheckConstraint("ck_workflow_run_tool_call_execution_identity", "(execution_attempt_id IS NULL AND execution_attempt_ordinal IS NULL AND execution_generation IS NULL) OR (execution_attempt_id IS NOT NULL AND execution_attempt_ordinal IS NOT NULL AND execution_attempt_ordinal > 0 AND (execution_generation IS NULL OR execution_generation > 0))");
            table.HasCheckConstraint("ck_workflow_run_tool_call_head", "call_ordinal > 0 AND attempt_count >= 0 AND next_attempt_ordinal = attempt_count + 1 AND revision > 0 AND schema_version > 0");
            table.HasCheckConstraint("ck_workflow_run_tool_call_identity", "tool_kind ~ '^[a-z0-9][a-z0-9._-]{0,126}/v[1-9][0-9]*$' AND btrim(tool_name) <> '' AND btrim(purpose) <> '' AND (tool_namespace IS NULL OR btrim(tool_namespace) <> '')");
            // Every comparison on a nullable column carries its own IS NOT NULL: a PostgreSQL CHECK admits a row that
            // evaluates to TRUE *or NULL*, so a bare `arguments_digest ~ '...'` would ADMIT the unverifiable reference
            // it exists to refuse. Kept spelled identically to 0141 so the two cannot drift.
            table.HasCheckConstraint("ck_workflow_run_tool_call_redaction", "(arguments_redaction IS NULL AND arguments_artifact_id IS NULL AND arguments_digest IS NULL AND redaction_policy IS NULL AND capture_completeness NOT IN ('Exact', 'RedactedExact')) OR (arguments_redaction IS NOT NULL AND arguments_redaction = 'Withheld' AND arguments_artifact_id IS NULL AND arguments_digest IS NULL AND redaction_policy IS NULL AND capture_completeness = 'Unavailable') OR (arguments_redaction IS NOT NULL AND arguments_redaction IN ('None', 'Masked') AND arguments_artifact_id IS NOT NULL AND arguments_digest IS NOT NULL AND arguments_digest ~ '^[0-9a-f]{64}$' AND redaction_policy IS NOT NULL AND btrim(redaction_policy) <> '' AND (arguments_redaction <> 'Masked' OR capture_completeness <> 'Exact'))");
            table.HasCheckConstraint("ck_workflow_run_tool_call_source_identity", "(source_kind IS NULL AND source_correlation_id IS NULL) OR (source_kind IS NOT NULL AND btrim(source_kind) <> '' AND source_correlation_id IS NOT NULL AND source_correlation_id <> '00000000-0000-0000-0000-000000000000'::uuid)");
            table.HasCheckConstraint("ck_workflow_run_tool_call_state", "state IN ('Pending', 'Running', 'Completed', 'Abandoned')");
            table.HasCheckConstraint("ck_workflow_run_tool_call_terminal", "(state IN ('Pending', 'Running') AND terminal_at IS NULL AND error_code IS NULL) OR (state = 'Completed' AND terminal_at IS NOT NULL AND attempt_count > 0) OR (state = 'Abandoned' AND terminal_at IS NOT NULL AND error_code IS NOT NULL)");
            table.HasCheckConstraint("ck_workflow_run_tool_call_time", "last_modified_at >= created_at AND (terminal_at IS NULL OR (terminal_at >= created_at AND last_modified_at >= terminal_at))");
            table.HasCheckConstraint("ck_workflow_run_tool_call_work_unit_identity", "(work_plan_id IS NULL AND plan_version IS NULL AND work_unit_id IS NULL AND work_unit_contract_hash IS NULL) OR (work_plan_id IS NOT NULL AND plan_version IS NOT NULL AND plan_version > 0 AND work_unit_id IS NOT NULL AND btrim(work_unit_id) <> '')");
        });
        builder.HasKey(call => call.Id);

        // Lets the attempt's composite foreign key prove its denormalized team/run scope belongs to THIS call, rather
        // than trusting a producer to stamp the same two values twice.
        builder.HasAlternateKey(call => new { call.Id, call.TeamId, call.WorkflowRunId }).HasName("ak_workflow_run_tool_call_scope");

        builder.Property(call => call.NodeId).HasMaxLength(256);
        builder.Property(call => call.IterationKey).HasMaxLength(1024);
        builder.Property(call => call.WorkUnitId).HasMaxLength(512);
        builder.Property(call => call.WorkUnitContractHash).HasMaxLength(128);
        builder.Property(call => call.Purpose).HasMaxLength(128);
        builder.Property(call => call.ToolKind).HasMaxLength(160);
        builder.Property(call => call.ToolNamespace).HasMaxLength(200);
        builder.Property(call => call.ToolName).HasMaxLength(200);
        builder.Property(call => call.EffectClass).HasConversion<string>().HasMaxLength(16);
        builder.Property(call => call.ArgumentsDigest).HasMaxLength(64);
        builder.Property(call => call.ArgumentsRedaction).HasConversion<string>().HasMaxLength(16);
        builder.Property(call => call.RedactionPolicy).HasMaxLength(200);
        builder.Property(call => call.SourceKind).HasMaxLength(64);
        builder.Property(call => call.CaptureSource).HasMaxLength(64);
        builder.Property(call => call.CaptureCompleteness).HasConversion<string>().HasMaxLength(20);
        builder.Property(call => call.State).HasConversion<string>().HasMaxLength(24);
        builder.Property(call => call.ErrorCode).HasMaxLength(128);
        builder.Property(call => call.ErrorMessage).HasMaxLength(2048);
        builder.Property(call => call.Xmin).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

        builder.HasOne<Team>().WithMany().HasForeignKey(call => call.TeamId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<WorkflowRun>().WithMany().HasForeignKey(call => new { call.TeamId, call.WorkflowRunId })
            .HasPrincipalKey(run => new { run.TeamId, run.Id }).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(call => new { call.WorkflowRunId, call.CreatedAt, call.Id }).HasDatabaseName("ix_workflow_run_tool_call_run_created");
        builder.HasIndex(call => new { call.TeamId, call.CreatedAt, call.Id }).HasDatabaseName("ix_workflow_run_tool_call_team_created");
        builder.HasIndex(call => new { call.ExecutionAttemptId, call.CallOrdinal }).HasDatabaseName("ix_workflow_run_tool_call_execution_attempt").HasFilter("execution_attempt_id IS NOT NULL");
        builder.HasIndex(call => new { call.WorkPlanId, call.PlanVersion, call.WorkUnitId }).HasDatabaseName("ix_workflow_run_tool_call_work_unit").HasFilter("work_plan_id IS NOT NULL");
        builder.HasIndex(call => new { call.ModelCallId, call.CallOrdinal }).HasDatabaseName("ix_workflow_run_tool_call_model_call").HasFilter("model_call_id IS NOT NULL");
        builder.HasIndex(call => new { call.TeamId, call.ToolKind, call.ToolName, call.CreatedAt }).HasDatabaseName("ix_workflow_run_tool_call_tool");

        // The audit's hottest question — every side effect in a window — kept partial so it does not grow with
        // read-only traffic, which is the overwhelming majority of tool calls.
        builder.HasIndex(call => new { call.TeamId, call.CreatedAt, call.Id }).HasDatabaseName("ix_workflow_run_tool_call_side_effecting").HasFilter("effect_class = 'SideEffecting'");

        // An invocation whose last attempt never reported is invisible to a created_at scan once the run is old.
        // Leading on last_modified_at with no team prefix so one sweep covers every tenant.
        builder.HasIndex(call => new { call.LastModifiedAt, call.TeamId, call.Id }).HasDatabaseName("ix_workflow_run_tool_call_stale_live").HasFilter("state IN ('Pending', 'Running')");
        builder.HasIndex(call => new { call.TeamId, call.WorkflowRunId, call.SourceKind, call.SourceCorrelationId }).IsUnique()
            .HasDatabaseName("ux_workflow_run_tool_call_source_identity").HasFilter("source_correlation_id IS NOT NULL");
    }
}

public sealed class WorkflowRunToolCallAttemptConfiguration : IEntityTypeConfiguration<WorkflowRunToolCallAttempt>
{
    public void Configure(EntityTypeBuilder<WorkflowRunToolCallAttempt> builder)
    {
        builder.ToTable(WorkflowRunDataNames.ToolCallAttempt, table =>
        {
            table.HasCheckConstraint("ck_workflow_run_tool_call_attempt_bounds", "attempt_ordinal > 0 AND revision > 0 AND schema_version > 0");
            table.HasCheckConstraint("ck_workflow_run_tool_call_attempt_capture_completeness", "capture_completeness IN ('Exact', 'RedactedExact', 'Partial', 'Unavailable', 'Corrupt', 'LegacyUnknown')");
            table.HasCheckConstraint("ck_workflow_run_tool_call_attempt_error", "(error_code IS NULL AND error_message IS NULL) OR (error_code IS NOT NULL AND btrim(error_code) <> '')");
            table.HasCheckConstraint("ck_workflow_run_tool_call_attempt_identity", "(transport_kind IS NULL OR btrim(transport_kind) <> '') AND (endpoint_fingerprint IS NULL OR btrim(endpoint_fingerprint) <> '') AND (invocation_id IS NULL OR btrim(invocation_id) <> '')");
            table.HasCheckConstraint("ck_workflow_run_tool_call_attempt_redaction", "(result_redaction IS NULL AND result_artifact_id IS NULL AND error_artifact_id IS NULL AND result_digest IS NULL AND error_digest IS NULL AND redaction_policy IS NULL AND capture_completeness NOT IN ('Exact', 'RedactedExact')) OR (result_redaction IS NOT NULL AND result_redaction = 'Withheld' AND result_artifact_id IS NULL AND error_artifact_id IS NULL AND result_digest IS NULL AND error_digest IS NULL AND redaction_policy IS NULL AND capture_completeness = 'Unavailable') OR (result_redaction IS NOT NULL AND result_redaction IN ('None', 'Masked') AND (result_artifact_id IS NOT NULL OR error_artifact_id IS NOT NULL) AND redaction_policy IS NOT NULL AND btrim(redaction_policy) <> '' AND (result_artifact_id IS NULL OR (result_digest IS NOT NULL AND result_digest ~ '^[0-9a-f]{64}$')) AND (result_artifact_id IS NOT NULL OR result_digest IS NULL) AND (error_artifact_id IS NULL OR (error_digest IS NOT NULL AND error_digest ~ '^[0-9a-f]{64}$')) AND (error_artifact_id IS NOT NULL OR error_digest IS NULL) AND (result_artifact_id IS NOT NULL OR capture_completeness NOT IN ('Exact', 'RedactedExact')) AND (result_redaction <> 'Masked' OR capture_completeness <> 'Exact'))");
            table.HasCheckConstraint("ck_workflow_run_tool_call_attempt_retry", "(retry_of_attempt_id IS NULL AND retry_reason IS NULL) OR (retry_of_attempt_id IS NOT NULL AND attempt_ordinal > 1 AND retry_reason IS NOT NULL AND btrim(retry_reason) <> '')");
            table.HasCheckConstraint("ck_workflow_run_tool_call_attempt_status", "status IN ('Pending', 'Running', 'Succeeded', 'Failed', 'Denied', 'Cancelled', 'TimedOut', 'Indeterminate')");
            table.HasCheckConstraint("ck_workflow_run_tool_call_attempt_terminal", "(status IN ('Pending', 'Running') AND completed_at IS NULL AND error_code IS NULL) OR (status = 'Succeeded' AND completed_at IS NOT NULL AND error_code IS NULL) OR (status IN ('Failed', 'Denied', 'Cancelled', 'TimedOut', 'Indeterminate') AND completed_at IS NOT NULL AND error_code IS NOT NULL)");
            table.HasCheckConstraint("ck_workflow_run_tool_call_attempt_time", "created_at >= started_at AND last_modified_at >= created_at AND (completed_at IS NULL OR (completed_at >= started_at AND last_modified_at >= completed_at))");
        });
        builder.HasKey(attempt => attempt.Id);

        // Lets RetryOfAttemptId's composite foreign key prove the retried attempt belongs to the SAME logical call.
        // Lower ordinal and non-liveness are the database guard's job: a foreign key proves membership, never order.
        builder.HasAlternateKey(attempt => new { attempt.Id, attempt.TeamId, attempt.ToolCallId }).HasName("ak_workflow_run_tool_call_attempt_scope");

        builder.Property(attempt => attempt.RetryReason).HasMaxLength(128);
        builder.Property(attempt => attempt.TransportKind).HasMaxLength(64);
        builder.Property(attempt => attempt.EndpointFingerprint).HasMaxLength(256);
        builder.Property(attempt => attempt.InvocationId).HasMaxLength(512);
        builder.Property(attempt => attempt.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(attempt => attempt.ResultDigest).HasMaxLength(64);
        builder.Property(attempt => attempt.ErrorDigest).HasMaxLength(64);
        builder.Property(attempt => attempt.ResultRedaction).HasConversion<string>().HasMaxLength(16);
        builder.Property(attempt => attempt.RedactionPolicy).HasMaxLength(200);
        builder.Property(attempt => attempt.CaptureSource).HasMaxLength(64);
        builder.Property(attempt => attempt.CaptureCompleteness).HasConversion<string>().HasMaxLength(20);
        builder.Property(attempt => attempt.ErrorCode).HasMaxLength(200);
        builder.Property(attempt => attempt.ErrorMessage).HasMaxLength(2048);
        builder.Property(attempt => attempt.Xmin).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

        builder.HasOne(attempt => attempt.ToolCall).WithMany(call => call.Attempts)
            .HasForeignKey(attempt => new { attempt.ToolCallId, attempt.TeamId, attempt.WorkflowRunId })
            .HasPrincipalKey(call => new { call.Id, call.TeamId, call.WorkflowRunId })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<WorkflowRunToolCallAttempt>().WithMany()
            .HasForeignKey(attempt => new { attempt.RetryOfAttemptId, attempt.TeamId, attempt.ToolCallId })
            .HasPrincipalKey(attempt => new { attempt.Id, attempt.TeamId, attempt.ToolCallId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(attempt => new { attempt.TeamId, attempt.ToolCallId, attempt.AttemptOrdinal }).IsUnique()
            .HasDatabaseName("ux_workflow_run_tool_call_attempt_ordinal");

        // The one-in-flight invariant's concurrency backstop. In one session the guard refuses a second live attempt
        // first, but two writers racing past their own snapshots see no conflict and only this index does.
        builder.HasIndex(attempt => new { attempt.TeamId, attempt.ToolCallId }).IsUnique()
            .HasDatabaseName("ux_workflow_run_tool_call_attempt_in_flight").HasFilter("status IN ('Pending', 'Running')");
        builder.HasIndex(attempt => new { attempt.TeamId, attempt.ToolCallId, attempt.InvocationId }).IsUnique()
            .HasDatabaseName("ux_workflow_run_tool_call_attempt_invocation").HasFilter("invocation_id IS NOT NULL");
        builder.HasIndex(attempt => new { attempt.WorkflowRunId, attempt.StartedAt, attempt.Id }).HasDatabaseName("ix_workflow_run_tool_call_attempt_run_started");
        builder.HasIndex(attempt => new { attempt.TeamId, attempt.StartedAt, attempt.Id }).HasDatabaseName("ix_workflow_run_tool_call_attempt_team_started");
        builder.HasIndex(attempt => attempt.RetryOfAttemptId).HasDatabaseName("ix_workflow_run_tool_call_attempt_retry").HasFilter("retry_of_attempt_id IS NOT NULL");
    }
}
