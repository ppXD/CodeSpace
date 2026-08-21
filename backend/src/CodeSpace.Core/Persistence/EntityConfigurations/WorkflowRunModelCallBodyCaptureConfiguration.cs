using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public sealed class WorkflowRunModelCallBodyCaptureConfiguration : IEntityTypeConfiguration<WorkflowRunModelCallBodyCapture>
{
    public void Configure(EntityTypeBuilder<WorkflowRunModelCallBodyCapture> builder)
    {
        builder.ToTable(WorkflowRunDataNames.ModelCallBodyCapture, table =>
        {
            table.HasCheckConstraint("ck_workflow_run_model_call_body_capture_artifact", "(state = 'Available' AND artifact_id IS NOT NULL AND source_sha256 ~ '^[0-9a-f]{64}$' AND size_bytes >= 0 AND content_type IS NOT NULL AND btrim(content_type) <> '') OR (state <> 'Available' AND artifact_id IS NULL AND source_sha256 IS NULL AND size_bytes IS NULL AND content_type IS NULL)");
            table.HasCheckConstraint("ck_workflow_run_model_call_body_capture_claim", "lease_fence >= 0 AND materialization_attempt_count >= 0 AND ((lease_owner_id IS NULL AND lease_expires_at IS NULL) OR (lease_owner_id IS NOT NULL AND lease_fence > 0 AND lease_expires_at IS NOT NULL))");
            table.HasCheckConstraint("ck_workflow_run_model_call_body_capture_error", "(last_error_code IS NULL AND last_error_message IS NULL) OR (last_error_code IS NOT NULL AND btrim(last_error_code) <> '')");
            table.HasCheckConstraint("ck_workflow_run_model_call_body_capture_identity", "source_kind = 'workflow-run-record/v1' AND ((body_kind = 'LogicalRequest' AND source_property = 'prompt') OR (body_kind = 'AttemptResponse' AND source_property = 'output') OR (body_kind = 'AttemptError' AND source_property = 'error'))");
            table.HasCheckConstraint("ck_workflow_run_model_call_body_capture_state", "state IN ('Pending', 'Available', 'NotRecorded', 'Corrupt', 'CaptureFailed', 'ExternalStateIndeterminate') AND ((state = 'Pending' AND terminal_at IS NULL) OR (state <> 'Pending' AND terminal_at IS NOT NULL AND lease_owner_id IS NULL))");
            table.HasCheckConstraint("ck_workflow_run_model_call_body_capture_time", "revision > 0 AND next_materialization_at >= created_at AND last_modified_at >= created_at AND (terminal_at IS NULL OR last_modified_at >= terminal_at)");
        });
        builder.HasKey(value => value.Id);
        builder.Property(value => value.BodyKind).HasConversion<string>().HasMaxLength(32);
        builder.Property(value => value.SourceKind).HasMaxLength(64);
        builder.Property(value => value.SourceProperty).HasMaxLength(32);
        builder.Property(value => value.State).HasConversion<string>().HasMaxLength(32);
        builder.Property(value => value.SourceSha256).HasMaxLength(64);
        builder.Property(value => value.ContentType).HasMaxLength(255);
        builder.Property(value => value.LastErrorCode).HasMaxLength(128);
        builder.Property(value => value.LastErrorMessage).HasMaxLength(2048);
        builder.Property(value => value.Revision).IsConcurrencyToken();
        builder.Property(value => value.Xmin).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

        builder.HasOne(value => value.ModelCall).WithMany()
            .HasForeignKey(value => new { value.ModelCallId, value.TeamId, value.WorkflowRunId })
            .HasPrincipalKey(value => new { value.Id, value.TeamId, value.WorkflowRunId }).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(value => value.ModelCallAttempt).WithMany().HasForeignKey(value => value.ModelCallAttemptId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(value => value.SourceRecord).WithMany().HasForeignKey(value => value.SourceRecordId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(value => new { value.ModelCallAttemptId, value.BodyKind }).IsUnique()
            .HasDatabaseName("ux_workflow_run_model_call_body_capture_identity");
        builder.HasIndex(value => new { value.NextMaterializationAt, value.TeamId, value.Id }).HasDatabaseName("ix_workflow_run_model_call_body_capture_pending")
            .HasFilter("state = 'Pending'").IncludeProperties(value => new { value.LeaseExpiresAt, value.LeaseFence });
        builder.HasIndex(value => new { value.TeamId, value.ArtifactId, value.Id }).HasDatabaseName("ix_workflow_run_model_call_body_capture_artifact")
            .HasFilter("artifact_id IS NOT NULL");
    }
}
