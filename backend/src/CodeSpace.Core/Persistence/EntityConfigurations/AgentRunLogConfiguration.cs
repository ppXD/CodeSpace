using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public sealed class AgentRunLogStreamConfiguration : IEntityTypeConfiguration<AgentRunLogStream>
{
    public void Configure(EntityTypeBuilder<AgentRunLogStream> builder)
    {
        builder.ToTable("agent_run_log_stream", table =>
        {
            table.HasCheckConstraint("ck_agent_run_log_stream_claim", "(worker_fence_epoch IS NULL AND capture_session_id IS NULL) OR (worker_fence_epoch IS NOT NULL AND worker_fence_epoch > 0 AND capture_session_id IS NOT NULL AND capture_session_id <> '00000000-0000-0000-0000-000000000000'::uuid)");
            table.HasCheckConstraint("ck_agent_run_log_stream_digest", "(content_digest_algorithm IS NULL AND content_digest IS NULL) OR (content_digest_algorithm IS NOT NULL AND content_digest_algorithm = 'Sha256' AND content_digest IS NOT NULL AND octet_length(content_digest) = 32)");
            table.HasCheckConstraint("ck_agent_run_log_stream_error", "(error_code IS NULL AND error_message IS NULL) OR (error_code IS NOT NULL AND btrim(error_code) <> '')");
            table.HasCheckConstraint("ck_agent_run_log_stream_head", "revision > 0 AND segment_count >= 0 AND total_bytes >= 0 AND source_offset_bytes >= 0 AND capture_source_base_offset_bytes >= 0 AND capture_source_base_offset_bytes <= source_offset_bytes AND next_segment_ordinal = segment_count + 1 AND next_offset_bytes = total_bytes AND schema_version > 0");
            table.HasCheckConstraint("ck_agent_run_log_stream_time", "last_modified_at >= created_at AND (capture_finalized_at IS NULL OR last_modified_at >= capture_finalized_at) AND (completed_at IS NULL OR last_modified_at >= completed_at)");
            table.HasCheckConstraint("ck_agent_run_log_stream_identity", "stream_kind ~ '^[a-z0-9][a-z0-9._/-]{0,126}/v[1-9][0-9]*$' AND capture_source ~ '^[a-z0-9][a-z0-9._/-]{0,126}/v[1-9][0-9]*$' AND content_type ~ '^[^[:space:]/]+/[^[:space:]]+$' AND (content_encoding IS NULL OR content_encoding ~ '^[a-z0-9][a-z0-9._+-]{0,63}$')");
            table.HasCheckConstraint("ck_agent_run_log_stream_retention", "retention IN ('Ephemeral', 'Run', 'Team', 'Compliance', 'Permanent') AND (expires_at IS NULL OR expires_at > created_at) AND (retention <> 'Ephemeral' OR expires_at IS NOT NULL) AND (retention <> 'Permanent' OR expires_at IS NULL)");
            table.HasCheckConstraint("ck_agent_run_log_stream_state", "state IN ('Open', 'Completed', 'Truncated', 'Unavailable', 'Corrupt', 'CaptureFailed')");
            table.HasCheckConstraint("ck_agent_run_log_stream_terminal", "((state = 'Open' AND completed_at IS NULL AND error_code IS NULL) OR (state = 'Completed' AND completed_at IS NOT NULL AND error_code IS NULL) OR (state IN ('Truncated', 'Unavailable', 'Corrupt', 'CaptureFailed') AND completed_at IS NOT NULL AND error_code IS NOT NULL)) AND (state <> 'Completed' OR (capture_finalized_at IS NOT NULL AND (schema_version = 1 OR (content_digest_algorithm = 'Sha256' AND content_digest IS NOT NULL AND octet_length(content_digest) = 32))))");
        });
        builder.HasKey(stream => stream.Id);
        builder.HasAlternateKey(stream => new { stream.TeamId, stream.Id, stream.AgentRunId }).HasName("ak_agent_run_log_stream_scope");
        builder.Property(stream => stream.StreamKind).HasMaxLength(128);
        builder.Property(stream => stream.ContentType).HasMaxLength(255);
        builder.Property(stream => stream.ContentEncoding).HasMaxLength(64);
        builder.Property(stream => stream.CaptureSource).HasMaxLength(128);
        builder.Property(stream => stream.Retention).HasConversion<string>().HasMaxLength(24);
        builder.Property(stream => stream.State).HasConversion<string>().HasMaxLength(24);
        builder.Property(stream => stream.ContentDigestAlgorithm).HasConversion<string>().HasMaxLength(16);
        builder.Property(stream => stream.ContentDigest).HasColumnType("bytea");
        builder.Property(stream => stream.ErrorCode).HasMaxLength(128);
        builder.Property(stream => stream.ErrorMessage).HasMaxLength(2048);
        builder.Property(stream => stream.Xmin).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

        builder.HasOne(stream => stream.AgentRun).WithMany()
            .HasForeignKey(stream => new { stream.TeamId, stream.AgentRunId })
            .HasPrincipalKey(run => new { run.TeamId, run.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(stream => new { stream.TeamId, stream.AgentRunId, stream.StreamKind }).IsUnique().HasDatabaseName("ux_agent_run_log_stream_kind");
        builder.HasIndex(stream => new { stream.TeamId, stream.State, stream.LastModifiedAt, stream.Id }).HasDatabaseName("ix_agent_run_log_stream_state_modified");
        builder.HasIndex(stream => new { stream.ExpiresAt, stream.Id }).HasDatabaseName("ix_agent_run_log_stream_expiry").HasFilter("expires_at IS NOT NULL");
    }
}

public sealed class AgentRunLogCaptureSessionConfiguration : IEntityTypeConfiguration<AgentRunLogCaptureSession>
{
    public void Configure(EntityTypeBuilder<AgentRunLogCaptureSession> builder)
    {
        builder.ToTable("agent_run_log_capture_session", table =>
        {
            table.HasCheckConstraint("ck_agent_run_log_capture_session_bounds", "initial_worker_fence_epoch > 0 AND current_worker_fence_epoch >= initial_worker_fence_epoch AND source_base_offset_bytes >= 0 AND source_offset_bytes >= source_base_offset_bytes AND revision > 0");
            table.HasCheckConstraint("ck_agent_run_log_capture_session_identity", "capture_session_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_agent_run_log_capture_session_state", "(state = 'Open' AND finalized_at IS NULL AND error_code IS NULL) OR (state = 'Finalized' AND finalized_at IS NOT NULL AND error_code IS NULL) OR (state = 'CaptureFailed' AND finalized_at IS NOT NULL AND error_code IS NOT NULL)");
            table.HasCheckConstraint("ck_agent_run_log_capture_session_time", "last_observed_at >= created_at AND (finalized_at IS NULL OR last_observed_at >= finalized_at)");
        });
        builder.HasKey(session => session.Id);
        builder.HasAlternateKey(session => new { session.TeamId, session.StreamId, session.CaptureSessionId }).HasName("ak_agent_run_log_capture_session_identity");
        builder.Property(session => session.State).HasConversion<string>().HasMaxLength(24);
        builder.Property(session => session.ErrorCode).HasMaxLength(128);
        builder.Property(session => session.ErrorMessage).HasMaxLength(2048);
        builder.Property(session => session.Xmin).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

        builder.HasOne(session => session.Stream).WithMany(stream => stream.CaptureSessions)
            .HasForeignKey(session => new { session.TeamId, session.StreamId, session.AgentRunId })
            .HasPrincipalKey(stream => new { stream.TeamId, stream.Id, stream.AgentRunId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(session => new { session.TeamId, session.AgentRunId, session.CreatedAt, session.Id }).HasDatabaseName("ix_agent_run_log_capture_session_run_created");
        builder.HasIndex(session => new { session.TeamId, session.State, session.LastObservedAt, session.Id }).HasDatabaseName("ix_agent_run_log_capture_session_state_observed");
    }
}

public sealed class AgentRunLogSegmentConfiguration : IEntityTypeConfiguration<AgentRunLogSegment>
{
    public void Configure(EntityTypeBuilder<AgentRunLogSegment> builder)
    {
        builder.ToTable("agent_run_log_segment", table =>
        {
            table.HasCheckConstraint("ck_agent_run_log_segment_bounds", "segment_ordinal > 0 AND start_offset_bytes >= 0 AND length_bytes > 0 AND source_start_offset_bytes >= 0 AND source_length_bytes > 0 AND worker_fence_epoch > 0 AND schema_version > 0");
            table.HasCheckConstraint("ck_agent_run_log_segment_identity", "capture_session_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_agent_run_log_segment_observation", "first_observed_at <= last_observed_at AND created_at >= last_observed_at");
        });
        builder.HasKey(segment => segment.Id);

        builder.HasOne(segment => segment.Stream).WithMany(stream => stream.Segments)
            .HasForeignKey(segment => new { segment.TeamId, segment.StreamId, segment.AgentRunId })
            .HasPrincipalKey(stream => new { stream.TeamId, stream.Id, stream.AgentRunId })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(segment => segment.CaptureSession).WithMany(session => session.Segments)
            .HasForeignKey(segment => new { segment.TeamId, segment.StreamId, segment.CaptureSessionId })
            .HasPrincipalKey(session => new { session.TeamId, session.StreamId, session.CaptureSessionId })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(segment => segment.ArtifactObject).WithMany()
            .HasForeignKey(segment => new { segment.TeamId, segment.ArtifactObjectId })
            .HasPrincipalKey(artifact => new { artifact.TeamId, artifact.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(segment => new { segment.TeamId, segment.StreamId, segment.SegmentOrdinal }).IsUnique().HasDatabaseName("ux_agent_run_log_segment_ordinal");
        builder.HasIndex(segment => new { segment.TeamId, segment.StreamId, segment.StartOffsetBytes }).IsUnique().HasDatabaseName("ux_agent_run_log_segment_offset");
        builder.HasIndex(segment => new { segment.TeamId, segment.ArtifactObjectId, segment.Id }).HasDatabaseName("ix_agent_run_log_segment_object");
    }
}

public sealed class AgentRunLogCaptureIntentConfiguration : IEntityTypeConfiguration<AgentRunLogCaptureIntent>
{
    public void Configure(EntityTypeBuilder<AgentRunLogCaptureIntent> builder)
    {
        builder.ToTable("agent_run_log_capture_intent", table =>
        {
            table.HasCheckConstraint("ck_agent_run_log_capture_intent_claim", "recovery_fence_epoch >= 0 AND recovery_attempt_count >= 0 AND ((recovery_attempt_count = 0 AND recovery_started_at IS NULL) OR (recovery_attempt_count > 0 AND recovery_started_at IS NOT NULL)) AND ((recovery_owner_id IS NULL AND recovery_lease_expires_at IS NULL) OR (recovery_owner_id IS NOT NULL AND recovery_fence_epoch > 0 AND recovery_lease_expires_at IS NOT NULL))");
            table.HasCheckConstraint("ck_agent_run_log_capture_intent_error", "(last_error_code IS NULL AND last_error_message IS NULL) OR (last_error_code IS NOT NULL AND btrim(last_error_code) <> '')");
            table.HasCheckConstraint("ck_agent_run_log_capture_intent_identity", "worker_fence_epoch > 0 AND capture_session_id <> '00000000-0000-0000-0000-000000000000'::uuid AND stream_kind ~ '^[a-z0-9][a-z0-9._/-]{0,126}/v[1-9][0-9]*$' AND capture_source ~ '^[a-z0-9][a-z0-9._/-]{0,126}/v[1-9][0-9]*$' AND content_type ~ '^[^[:space:]/]+/[^[:space:]]+$' AND (content_encoding IS NULL OR content_encoding ~ '^[a-z0-9][a-z0-9._+-]{0,63}$')");
            table.HasCheckConstraint("ck_agent_run_log_capture_intent_state", "state IN ('Expected', 'Opened', 'SourceFinalized', 'Completed', 'CaptureFailed', 'Superseded', 'ExternalStateIndeterminate') AND ((state IN ('Completed', 'CaptureFailed', 'Superseded', 'ExternalStateIndeterminate') AND terminal_at IS NOT NULL AND recovery_owner_id IS NULL) OR (state IN ('Expected', 'Opened', 'SourceFinalized') AND terminal_at IS NULL)) AND (state IN ('Expected', 'CaptureFailed', 'Superseded', 'ExternalStateIndeterminate') OR stream_id IS NOT NULL)");
            table.HasCheckConstraint("ck_agent_run_log_capture_intent_time", "revision > 0 AND next_recovery_at >= created_at AND last_modified_at >= created_at AND (recovery_started_at IS NULL OR last_modified_at >= recovery_started_at) AND (terminal_observed_at IS NULL OR last_modified_at >= terminal_observed_at) AND (terminal_at IS NULL OR last_modified_at >= terminal_at)");
        });
        builder.HasKey(intent => intent.Id);
        builder.Property(intent => intent.StreamKind).HasMaxLength(128);
        builder.Property(intent => intent.ContentType).HasMaxLength(255);
        builder.Property(intent => intent.ContentEncoding).HasMaxLength(64);
        builder.Property(intent => intent.CaptureSource).HasMaxLength(128);
        builder.Property(intent => intent.State).HasConversion<string>().HasMaxLength(32);
        builder.Property(intent => intent.LastErrorCode).HasMaxLength(128);
        builder.Property(intent => intent.LastErrorMessage).HasMaxLength(2048);
        builder.Property(intent => intent.Xmin).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

        builder.HasOne(intent => intent.AgentRun).WithMany()
            .HasForeignKey(intent => new { intent.TeamId, intent.AgentRunId })
            .HasPrincipalKey(run => new { run.TeamId, run.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(intent => intent.Stream).WithMany(stream => stream.CaptureIntents)
            .HasForeignKey(intent => new { intent.TeamId, intent.StreamId, intent.AgentRunId })
            .HasPrincipalKey(stream => new { stream.TeamId, stream.Id, stream.AgentRunId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(intent => new { intent.TeamId, intent.AgentRunId, intent.WorkerFenceEpoch, intent.CaptureSessionId, intent.StreamKind }).IsUnique().HasDatabaseName("ux_agent_run_log_capture_intent_identity");
        builder.HasIndex(intent => new { intent.NextRecoveryAt, intent.TeamId, intent.Id }).HasDatabaseName("ix_agent_run_log_capture_intent_recovery")
            .HasFilter("state IN ('Expected', 'Opened', 'SourceFinalized')").IncludeProperties(intent => new { intent.RecoveryLeaseExpiresAt, intent.WorkerFenceEpoch });
    }
}
