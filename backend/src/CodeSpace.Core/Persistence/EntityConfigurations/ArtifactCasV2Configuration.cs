using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public sealed class ArtifactObjectConfiguration : IEntityTypeConfiguration<ArtifactObject>
{
    public void Configure(EntityTypeBuilder<ArtifactObject> builder)
    {
        builder.ToTable("artifact_object", table =>
        {
            table.HasCheckConstraint("ck_artifact_object_digest", "digest_algorithm IN ('Sha256') AND octet_length(digest) = 32");
            table.HasCheckConstraint("ck_artifact_object_size", "size_bytes >= 0");
        });
        builder.HasKey(o => o.Id);
        builder.HasAlternateKey(o => new { o.TeamId, o.Id }).HasName("ak_artifact_object_team_id");
        builder.Property(o => o.DigestAlgorithm).HasConversion<string>().HasMaxLength(16);
        builder.Property(o => o.Digest).HasColumnType("bytea");
        builder.HasOne(o => o.Team).WithMany().HasForeignKey(o => o.TeamId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(o => new { o.TeamId, o.DigestAlgorithm, o.Digest }).IsUnique().HasDatabaseName("ux_artifact_object_digest");
        builder.HasIndex(o => new { o.TeamId, o.CreatedDate, o.Id }).HasDatabaseName("ix_artifact_object_team_created");
    }
}

public sealed class ArtifactLocationConfiguration : IEntityTypeConfiguration<ArtifactLocation>
{
    public void Configure(EntityTypeBuilder<ArtifactLocation> builder)
    {
        builder.ToTable("artifact_location", table =>
        {
            table.HasCheckConstraint("ck_artifact_location_checksum", "(provider_checksum_algorithm IS NULL AND provider_checksum IS NULL) OR (provider_checksum_algorithm ~ '^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}$' AND provider_checksum IS NOT NULL AND octet_length(provider_checksum) > 0)");
            table.HasCheckConstraint("ck_artifact_location_encoding", "content_encoding IS NULL OR content_encoding ~ '^[a-z0-9][a-z0-9._+-]{0,63}$'");
            table.HasCheckConstraint("ck_artifact_location_error", "(last_error_code IS NULL AND last_error_message IS NULL) OR (last_error_code IS NOT NULL AND btrim(last_error_code) <> '')");
            table.HasCheckConstraint("ck_artifact_location_identity", "btrim(locator) <> '' AND btrim(object_key) <> ''");
            table.HasCheckConstraint("ck_artifact_location_observation", "(observed_size_bytes IS NULL OR observed_size_bytes >= 0) AND (verified_at IS NULL OR verified_at >= created_date) AND (state <> 'Available' OR (verified_at IS NOT NULL AND observed_size_bytes IS NOT NULL AND provider_checksum_algorithm = 'Sha256' AND provider_checksum IS NOT NULL AND octet_length(provider_checksum) = 32 AND last_error_code IS NULL))");
            table.HasCheckConstraint("ck_artifact_location_revision", "revision > 0");
            table.HasCheckConstraint("ck_artifact_location_state", "state IN ('Pending', 'Available', 'Missing', 'Corrupt', 'Deleting', 'Deleted', 'Failed')");
        });
        builder.HasKey(l => l.Id);
        builder.HasAlternateKey(l => new { l.TeamId, l.Id }).HasName("ak_artifact_location_team_id");
        builder.Property(l => l.Locator).HasMaxLength(2048);
        builder.Property(l => l.ObjectKey).HasMaxLength(2048);
        builder.Property(l => l.ProviderObjectVersion).HasMaxLength(512);
        builder.Property(l => l.ProviderETag).HasColumnName("provider_etag").HasMaxLength(512);
        builder.Property(l => l.ProviderChecksumAlgorithm).HasMaxLength(64);
        builder.Property(l => l.ProviderChecksum).HasColumnType("bytea");
        builder.Property(l => l.ContentEncoding).HasMaxLength(64);
        builder.Property(l => l.EncryptionKeyVersion).HasMaxLength(512);
        builder.Property(l => l.State).HasConversion<string>().HasMaxLength(24);
        builder.Property(l => l.LastErrorCode).HasMaxLength(128);
        builder.Property(l => l.LastErrorMessage).HasMaxLength(2048);
        builder.Property(l => l.Xmin).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

        builder.HasOne(l => l.ArtifactObject).WithMany(o => o.Locations)
            .HasForeignKey(l => new { l.TeamId, l.ArtifactObjectId })
            .HasPrincipalKey(o => new { o.TeamId, o.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(l => l.StorageProfileRevision).WithMany()
            .HasForeignKey(l => new { l.TeamId, l.StorageProfileRevisionId })
            .HasPrincipalKey(r => new { r.TeamId, r.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(l => new { l.TeamId, l.StorageProfileRevisionId, l.ObjectKey }).IsUnique().HasDatabaseName("ux_artifact_location_profile_object_key");
        builder.HasIndex(l => new { l.TeamId, l.ArtifactObjectId, l.State }).HasDatabaseName("ix_artifact_location_object_state");
        builder.HasIndex(l => new { l.TeamId, l.State, l.VerifiedAt, l.Id }).HasDatabaseName("ix_artifact_location_state_verified");
    }
}

public sealed class ArtifactLocationEventConfiguration : IEntityTypeConfiguration<ArtifactLocationEvent>
{
    public void Configure(EntityTypeBuilder<ArtifactLocationEvent> builder)
    {
        builder.ToTable("artifact_location_event", table =>
        {
            table.HasCheckConstraint("ck_artifact_location_event_checksum", "(provider_checksum_algorithm IS NULL AND provider_checksum IS NULL) OR (provider_checksum_algorithm ~ '^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}$' AND provider_checksum IS NOT NULL AND octet_length(provider_checksum) > 0)");
            table.HasCheckConstraint("ck_artifact_location_event_details", "jsonb_typeof(details_jsonb) = 'object'");
            table.HasCheckConstraint("ck_artifact_location_event_error", "(error_code IS NULL AND error_message IS NULL) OR (error_code IS NOT NULL AND btrim(error_code) <> '')");
            table.HasCheckConstraint("ck_artifact_location_event_revision", "revision > 0 AND (observed_size_bytes IS NULL OR observed_size_bytes >= 0)");
            table.HasCheckConstraint("ck_artifact_location_event_type", "event_type IN ('Created', 'Observed', 'Verified', 'StateChanged', 'Failed')");
            table.HasCheckConstraint("ck_artifact_location_event_state", "state IN ('Pending', 'Available', 'Missing', 'Corrupt', 'Deleting', 'Deleted', 'Failed')");
        });
        builder.HasKey(e => e.Id);
        builder.Property(e => e.EventType).HasConversion<string>().HasMaxLength(24);
        builder.Property(e => e.State).HasConversion<string>().HasMaxLength(24);
        builder.Property(e => e.ProviderObjectVersion).HasMaxLength(512);
        builder.Property(e => e.ProviderETag).HasColumnName("provider_etag").HasMaxLength(512);
        builder.Property(e => e.ProviderChecksumAlgorithm).HasMaxLength(64);
        builder.Property(e => e.ProviderChecksum).HasColumnType("bytea");
        builder.Property(e => e.ErrorCode).HasMaxLength(128);
        builder.Property(e => e.ErrorMessage).HasMaxLength(2048);
        builder.Property(e => e.DetailsJson).HasColumnName("details_jsonb").HasColumnType("jsonb");

        builder.HasOne(e => e.ArtifactLocation).WithMany(l => l.Events)
            .HasForeignKey(e => new { e.TeamId, e.ArtifactLocationId })
            .HasPrincipalKey(l => new { l.TeamId, l.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => new { e.TeamId, e.ArtifactLocationId, e.Revision }).IsUnique().HasDatabaseName("ux_artifact_location_event_revision");
        builder.HasIndex(e => new { e.TeamId, e.ObservedAt, e.Id }).HasDatabaseName("ix_artifact_location_event_team_observed");
    }
}

public sealed class ArtifactTransferIntentConfiguration : IEntityTypeConfiguration<ArtifactTransferIntent>
{
    public void Configure(EntityTypeBuilder<ArtifactTransferIntent> builder)
    {
        builder.ToTable("artifact_transfer_intent", table =>
        {
            table.HasCheckConstraint("ck_artifact_transfer_intent_attempt", "(execution_attempt_id IS NULL AND execution_attempt_ordinal IS NULL AND execution_generation IS NULL AND worker_fence_epoch IS NULL) OR (execution_attempt_id IS NOT NULL AND execution_attempt_ordinal IS NOT NULL AND execution_attempt_ordinal > 0 AND execution_generation IS NOT NULL AND execution_generation > 0 AND worker_fence_epoch IS NOT NULL AND worker_fence_epoch > 0)");
            table.HasCheckConstraint("ck_artifact_transfer_intent_digest", "expected_digest_algorithm IN ('Sha256') AND octet_length(expected_digest) = 32 AND expected_size_bytes >= 0");
            table.HasCheckConstraint("ck_artifact_transfer_intent_error", "(last_error_code IS NULL AND last_error_message IS NULL) OR (last_error_code IS NOT NULL AND btrim(last_error_code) <> '')");
            table.HasCheckConstraint("ck_artifact_transfer_intent_identity", "btrim(idempotency_key) <> '' AND btrim(target_locator) <> '' AND btrim(target_object_key) <> '' AND (temporary_object_key IS NULL OR btrim(temporary_object_key) <> '') AND (provider_upload_id IS NULL OR btrim(provider_upload_id) <> '')");
            table.HasCheckConstraint("ck_artifact_transfer_intent_outcome", "(state = 'Committed' AND artifact_object_id IS NOT NULL AND artifact_location_id IS NOT NULL AND completed_at IS NOT NULL) OR (state IN ('Failed', 'Cancelled') AND artifact_object_id IS NULL AND artifact_location_id IS NULL AND completed_at IS NOT NULL) OR (state NOT IN ('Committed', 'Failed', 'Cancelled') AND artifact_object_id IS NULL AND artifact_location_id IS NULL AND completed_at IS NULL)");
            table.HasCheckConstraint("ck_artifact_transfer_intent_retry", "retry_count >= 0 AND ((state = 'RetryScheduled' AND next_attempt_at IS NOT NULL AND last_error_code IS NOT NULL) OR (state <> 'RetryScheduled' AND next_attempt_at IS NULL))");
            table.HasCheckConstraint("ck_artifact_transfer_intent_revision", "revision > 0 AND (completed_at IS NULL OR completed_at >= created_date)");
            table.HasCheckConstraint("ck_artifact_transfer_intent_state", "state IN ('Intended', 'Uploading', 'Uploaded', 'Verifying', 'RetryScheduled', 'Committed', 'Failed', 'Cancelled')");
        });
        builder.HasKey(i => i.Id);
        builder.Property(i => i.IdempotencyKey).HasMaxLength(256);
        builder.Property(i => i.ExpectedDigestAlgorithm).HasConversion<string>().HasMaxLength(16);
        builder.Property(i => i.ExpectedDigest).HasColumnType("bytea");
        builder.Property(i => i.TargetLocator).HasMaxLength(2048);
        builder.Property(i => i.TargetObjectKey).HasMaxLength(2048);
        builder.Property(i => i.TemporaryObjectKey).HasMaxLength(2048);
        builder.Property(i => i.ProviderUploadId).HasMaxLength(1024);
        builder.Property(i => i.State).HasConversion<string>().HasMaxLength(24);
        builder.Property(i => i.LastErrorCode).HasMaxLength(128);
        builder.Property(i => i.LastErrorMessage).HasMaxLength(2048);
        builder.Property(i => i.Xmin).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

        builder.HasOne(i => i.StorageProfileRevision).WithMany()
            .HasForeignKey(i => new { i.TeamId, i.StorageProfileRevisionId })
            .HasPrincipalKey(r => new { r.TeamId, r.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(i => i.ArtifactObject).WithMany()
            .HasForeignKey(i => new { i.TeamId, i.ArtifactObjectId })
            .HasPrincipalKey(o => new { o.TeamId, o.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(i => i.ArtifactLocation).WithMany()
            .HasForeignKey(i => new { i.TeamId, i.ArtifactLocationId })
            .HasPrincipalKey(l => new { l.TeamId, l.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(i => new { i.TeamId, i.StorageProfileRevisionId, i.IdempotencyKey }).IsUnique().HasDatabaseName("ux_artifact_transfer_intent_idempotency");
        builder.HasIndex(i => new { i.TeamId, i.State, i.NextAttemptAt, i.Id }).HasDatabaseName("ix_artifact_transfer_intent_state_next");
        builder.HasIndex(i => new { i.TeamId, i.ExpectedDigestAlgorithm, i.ExpectedDigest }).HasDatabaseName("ix_artifact_transfer_intent_expected_digest");
    }
}

public sealed class WorkflowRunArtifactReferenceConfiguration : IEntityTypeConfiguration<WorkflowRunArtifactReference>
{
    public void Configure(EntityTypeBuilder<WorkflowRunArtifactReference> builder)
    {
        builder.ToTable("workflow_run_artifact_reference", table =>
        {
            table.HasCheckConstraint("ck_run_artifact_reference_attempt", "(execution_attempt_id IS NULL AND execution_attempt_ordinal IS NULL AND execution_generation IS NULL) OR (execution_attempt_id IS NOT NULL AND execution_attempt_ordinal IS NOT NULL AND execution_attempt_ordinal > 0 AND execution_generation IS NOT NULL AND execution_generation > 0)");
            table.HasCheckConstraint("ck_run_artifact_reference_content_type", "content_type ~ '^[^[:space:]/]+/[^[:space:]]+$'");
            table.HasCheckConstraint("ck_run_artifact_reference_expiry", "(expires_at IS NULL OR expires_at > created_date) AND (retention <> 'Ephemeral' OR expires_at IS NOT NULL) AND (retention <> 'Permanent' OR expires_at IS NULL)");
            table.HasCheckConstraint("ck_run_artifact_reference_path", "btrim(logical_path) <> '' AND logical_path !~ '(^/|(^|/)\\.\\.(/|$)|\\\\)'");
            table.HasCheckConstraint("ck_run_artifact_reference_retention", "retention IN ('Ephemeral', 'Run', 'Team', 'Compliance', 'Permanent')");
            table.HasCheckConstraint("ck_run_artifact_reference_role", "role ~ '^[a-z0-9][a-z0-9._/-]{0,127}$'");
            table.HasCheckConstraint("ck_run_artifact_reference_superseded", "superseded_by_reference_id IS NULL OR superseded_by_reference_id <> id");
            table.HasCheckConstraint("ck_run_artifact_reference_work_unit", "(work_plan_id IS NULL AND plan_version IS NULL AND work_unit_id IS NULL AND work_unit_contract_hash IS NULL AND requirement_revision IS NULL) OR (work_plan_id IS NOT NULL AND plan_version IS NOT NULL AND plan_version > 0 AND work_unit_id IS NOT NULL AND btrim(work_unit_id) <> '' AND (requirement_revision IS NULL OR requirement_revision > 0))");
        });
        builder.HasKey(r => r.Id);
        builder.HasAlternateKey(r => new { r.TeamId, r.Id }).HasName("ak_run_artifact_reference_team_id");
        builder.Property(r => r.NodeId).HasMaxLength(256);
        builder.Property(r => r.IterationKey).HasMaxLength(1024);
        builder.Property(r => r.WorkUnitId).HasMaxLength(512);
        builder.Property(r => r.WorkUnitContractHash).HasMaxLength(128);
        builder.Property(r => r.Role).HasMaxLength(128);
        builder.Property(r => r.LogicalPath).HasMaxLength(2048);
        builder.Property(r => r.ContentType).HasMaxLength(255);
        builder.Property(r => r.Retention).HasConversion<string>().HasMaxLength(24);

        builder.HasOne(r => r.ArtifactObject).WithMany()
            .HasForeignKey(r => new { r.TeamId, r.ArtifactObjectId })
            .HasPrincipalKey(o => new { o.TeamId, o.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(r => r.WorkflowRun).WithMany()
            .HasForeignKey(r => new { r.TeamId, r.WorkflowRunId })
            .HasPrincipalKey(run => new { run.TeamId, run.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(r => r.WorkPlan).WithMany()
            .HasForeignKey(r => new { r.TeamId, r.WorkPlanId, r.WorkflowRunId, r.PlanVersion })
            .HasPrincipalKey(plan => new { plan.TeamId, plan.Id, plan.WorkflowRunId, plan.Version })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(r => r.SupersededByReference).WithMany()
            .HasForeignKey(r => new { r.TeamId, r.SupersededByReferenceId })
            .HasPrincipalKey(reference => new { reference.TeamId, reference.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(r => new { r.TeamId, r.WorkflowRunId, r.Role, r.LogicalPath, r.Id }).HasDatabaseName("ix_run_artifact_reference_active").HasFilter("superseded_by_reference_id IS NULL");
        builder.HasIndex(r => new { r.TeamId, r.ArtifactObjectId, r.Id }).HasDatabaseName("ix_run_artifact_reference_object");
        builder.HasIndex(r => new { r.WorkPlanId, r.PlanVersion, r.WorkUnitId, r.Id }).HasDatabaseName("ix_run_artifact_reference_work_unit").HasFilter("work_plan_id IS NOT NULL");
        builder.HasIndex(r => new { r.ExecutionAttemptId, r.ExecutionGeneration, r.Id }).HasDatabaseName("ix_run_artifact_reference_attempt").HasFilter("execution_attempt_id IS NOT NULL");
        builder.HasIndex(r => new { r.ExpiresAt, r.Id }).HasDatabaseName("ix_run_artifact_reference_expiry").HasFilter("expires_at IS NOT NULL AND superseded_by_reference_id IS NULL");
        builder.HasIndex(r => new { r.TeamId, r.WorkflowRunId, r.ExecutionAttemptId, r.Role, r.LogicalPath }).IsUnique().HasDatabaseName("ux_run_artifact_reference_attempt_path").HasFilter("execution_attempt_id IS NOT NULL");
    }
}
