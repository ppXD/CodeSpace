using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public sealed class WorkflowRunModelCallConfiguration : IEntityTypeConfiguration<WorkflowRunModelCall>
{
    public void Configure(EntityTypeBuilder<WorkflowRunModelCall> builder)
    {
        builder.ToTable(WorkflowRunDataNames.ModelCall, table =>
        {
            table.HasCheckConstraint("ck_workflow_run_model_call_capture_completeness", "capture_completeness IN ('Exact', 'RedactedExact', 'Partial', 'Unavailable', 'Corrupt', 'LegacyUnknown')");
            table.HasCheckConstraint("ck_workflow_run_model_call_execution_identity", "(execution_attempt_id IS NULL AND execution_attempt_ordinal IS NULL AND execution_generation IS NULL) OR (execution_attempt_id IS NOT NULL AND execution_attempt_ordinal IS NOT NULL AND execution_attempt_ordinal > 0 AND (execution_generation IS NULL OR execution_generation > 0))");
            table.HasCheckConstraint("ck_workflow_run_model_call_positive_values", "call_ordinal > 0 AND schema_version > 0");
            table.HasCheckConstraint("ck_workflow_run_model_call_provenance", "btrim(purpose) <> '' AND (selection_policy IS NULL OR btrim(selection_policy) <> '')");
            table.HasCheckConstraint("ck_workflow_run_model_call_source_identity", "(source_kind IS NULL AND source_correlation_id IS NULL) OR (source_kind IS NOT NULL AND btrim(source_kind) <> '' AND source_correlation_id IS NOT NULL AND source_correlation_id <> '00000000-0000-0000-0000-000000000000'::uuid)");
            table.HasCheckConstraint("ck_workflow_run_model_call_work_unit_identity", "(work_plan_id IS NULL AND plan_version IS NULL AND work_unit_id IS NULL AND work_unit_contract_hash IS NULL) OR (work_plan_id IS NOT NULL AND plan_version IS NOT NULL AND plan_version > 0 AND work_unit_id IS NOT NULL AND btrim(work_unit_id) <> '')");
        });
        builder.HasKey(c => c.Id);

        // The redundant-looking alternate key lets the attempt's composite FK prove its denormalized team/run scope
        // belongs to this exact call rather than trusting a producer to stamp the same values twice.
        builder.HasAlternateKey(c => new { c.Id, c.TeamId, c.WorkflowRunId }).HasName("ak_workflow_run_model_call_scope");

        builder.Property(c => c.NodeId).HasMaxLength(256);
        builder.Property(c => c.IterationKey).HasMaxLength(1024);
        builder.Property(c => c.WorkUnitId).HasMaxLength(512);
        builder.Property(c => c.WorkUnitContractHash).HasMaxLength(128);
        builder.Property(c => c.SourceKind).HasMaxLength(64);
        builder.Property(c => c.Purpose).HasMaxLength(128);
        builder.Property(c => c.RequestedProvider).HasMaxLength(100);
        builder.Property(c => c.RequestedModel).HasMaxLength(500);
        builder.Property(c => c.SelectionPolicy).HasMaxLength(256);
        builder.Property(c => c.CaptureSource).HasMaxLength(64);
        builder.Property(c => c.CaptureCompleteness).HasConversion<string>().HasMaxLength(20);

        builder.HasOne<Team>().WithMany().HasForeignKey(c => c.TeamId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<WorkflowRun>().WithMany().HasForeignKey(c => new { c.TeamId, c.WorkflowRunId })
            .HasPrincipalKey(r => new { r.TeamId, r.Id }).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(c => new { c.WorkflowRunId, c.CreatedDate, c.Id }).HasDatabaseName("ix_workflow_run_model_call_run_created");
        builder.HasIndex(c => new { c.TeamId, c.CreatedDate, c.Id }).HasDatabaseName("ix_workflow_run_model_call_team_created");
        builder.HasIndex(c => new { c.ExecutionAttemptId, c.CallOrdinal }).HasDatabaseName("ix_workflow_run_model_call_execution_attempt").HasFilter("execution_attempt_id IS NOT NULL");
        builder.HasIndex(c => new { c.WorkPlanId, c.PlanVersion, c.WorkUnitId }).HasDatabaseName("ix_workflow_run_model_call_work_unit").HasFilter("work_plan_id IS NOT NULL");
        builder.HasIndex(c => new { c.RequestedModelRowId, c.CreatedDate }).HasDatabaseName("ix_workflow_run_model_call_requested_model_row").HasFilter("requested_model_row_id IS NOT NULL");
        builder.HasIndex(c => new { c.TeamId, c.WorkflowRunId, c.SourceKind, c.SourceCorrelationId }).IsUnique()
            .HasDatabaseName("ux_workflow_run_model_call_source_identity").HasFilter("source_correlation_id IS NOT NULL");
    }
}

public sealed class WorkflowRunModelCallAttemptConfiguration : IEntityTypeConfiguration<WorkflowRunModelCallAttempt>
{
    public void Configure(EntityTypeBuilder<WorkflowRunModelCallAttempt> builder)
    {
        builder.ToTable(WorkflowRunDataNames.ModelCallAttempt, table =>
        {
            table.HasCheckConstraint("ck_workflow_run_model_call_attempt_capture_completeness", "capture_completeness IN ('Exact', 'RedactedExact', 'Partial', 'Unavailable', 'Corrupt', 'LegacyUnknown')");
            table.HasCheckConstraint("ck_workflow_run_model_call_attempt_cost", "(cost_amount IS NULL AND cost_currency IS NULL) OR (cost_amount IS NOT NULL AND cost_amount >= 0 AND cost_currency IS NOT NULL AND cost_currency ~ '^[A-Z]{3}$')");
            table.HasCheckConstraint("ck_workflow_run_model_call_attempt_http_status", "http_status_code IS NULL OR http_status_code BETWEEN 100 AND 599");
            table.HasCheckConstraint("ck_workflow_run_model_call_attempt_positive_values", "attempt_ordinal > 0 AND schema_version > 0 AND (input_tokens IS NULL OR input_tokens >= 0) AND (output_tokens IS NULL OR output_tokens >= 0) AND (cache_read_tokens IS NULL OR cache_read_tokens >= 0) AND (cache_write_tokens IS NULL OR cache_write_tokens >= 0) AND (reasoning_tokens IS NULL OR reasoning_tokens >= 0)");
            table.HasCheckConstraint("ck_workflow_run_model_call_attempt_source_identity", "(source_started_record_id IS NULL AND source_terminal_record_id IS NULL AND source_evidence_revision = 0) OR (source_terminal_record_id IS NOT NULL AND source_evidence_revision > 0)");
            table.HasCheckConstraint("ck_workflow_run_model_call_attempt_status", "status IN ('Pending', 'Running', 'Succeeded', 'Failed', 'Cancelled', 'TimedOut', 'Indeterminate')");
            table.HasCheckConstraint("ck_workflow_run_model_call_attempt_timing", "(first_token_at IS NULL OR first_token_at >= started_at) AND (completed_at IS NULL OR completed_at >= started_at) AND (first_token_at IS NULL OR completed_at IS NULL OR first_token_at <= completed_at)");
        });
        builder.HasKey(a => a.Id);

        builder.Property(a => a.EffectiveProvider).HasMaxLength(100);
        builder.Property(a => a.EffectiveModel).HasMaxLength(500);
        builder.Property(a => a.TransportKind).HasMaxLength(64);
        builder.Property(a => a.EndpointFingerprint).HasMaxLength(256);
        builder.Property(a => a.ProviderRequestId).HasMaxLength(512);
        builder.Property(a => a.Status).HasMaxLength(32);
        builder.Property(a => a.ErrorCode).HasMaxLength(200);
        builder.Property(a => a.FinishReason).HasMaxLength(100);
        builder.Property(a => a.CaptureSource).HasMaxLength(64);
        builder.Property(a => a.CaptureCompleteness).HasConversion<string>().HasMaxLength(20);
        builder.Property(a => a.CostAmount).HasPrecision(18, 8);
        builder.Property(a => a.CostCurrency).HasMaxLength(3);
        builder.Property(a => a.PricingVersion).HasMaxLength(200);
        builder.Property(a => a.SourceEvidenceRevision).IsConcurrencyToken();

        builder.HasOne<WorkflowRunModelCall>().WithMany()
            .HasForeignKey(a => new { a.ModelCallId, a.TeamId, a.WorkflowRunId })
            .HasPrincipalKey(c => new { c.Id, c.TeamId, c.WorkflowRunId })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<WorkflowRunRecord>().WithMany()
            .HasForeignKey(a => a.SourceStartedRecordId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<WorkflowRunRecord>().WithMany()
            .HasForeignKey(a => a.SourceTerminalRecordId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(a => new { a.ModelCallId, a.AttemptOrdinal }).IsUnique().HasDatabaseName("ux_workflow_run_model_call_attempt_ordinal");
        builder.HasIndex(a => new { a.WorkflowRunId, a.StartedAt, a.Id }).HasDatabaseName("ix_workflow_run_model_call_attempt_run_started");
        builder.HasIndex(a => new { a.TeamId, a.StartedAt, a.Id }).HasDatabaseName("ix_workflow_run_model_call_attempt_team_started");
        builder.HasIndex(a => new { a.EffectiveProvider, a.ProviderRequestId }).HasDatabaseName("ix_workflow_run_model_call_attempt_provider_request").HasFilter("provider_request_id IS NOT NULL");
        builder.HasIndex(a => new { a.EffectiveModelRowId, a.StartedAt }).HasDatabaseName("ix_workflow_run_model_call_attempt_effective_model_row").HasFilter("effective_model_row_id IS NOT NULL");
        builder.HasIndex(a => new { a.TeamId, a.WorkflowRunId, a.SourceStartedRecordId }).IsUnique()
            .HasDatabaseName("ux_workflow_run_model_call_attempt_source_started").HasFilter("source_started_record_id IS NOT NULL");
        builder.HasIndex(a => new { a.TeamId, a.WorkflowRunId, a.SourceTerminalRecordId }).IsUnique()
            .HasDatabaseName("ux_workflow_run_model_call_attempt_source_terminal").HasFilter("source_terminal_record_id IS NOT NULL");
        builder.HasIndex(a => new { a.WorkflowRunId, a.ModelCallId }).HasDatabaseName("ix_workflow_run_model_call_attempt_late_start")
            .HasFilter("source_terminal_record_id IS NOT NULL AND source_started_record_id IS NULL");
    }
}
