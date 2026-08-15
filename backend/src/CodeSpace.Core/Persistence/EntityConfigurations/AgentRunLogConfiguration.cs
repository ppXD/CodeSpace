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
            table.HasCheckConstraint("ck_agent_run_log_stream_head", "revision > 0 AND segment_count >= 0 AND total_bytes >= 0 AND next_segment_ordinal = segment_count + 1 AND next_offset_bytes = total_bytes AND schema_version > 0");
            table.HasCheckConstraint("ck_agent_run_log_stream_time", "last_modified_at >= created_at AND (completed_at IS NULL OR last_modified_at >= completed_at)");
            table.HasCheckConstraint("ck_agent_run_log_stream_identity", "stream_kind ~ '^[a-z0-9][a-z0-9._/-]{0,126}/v[1-9][0-9]*$' AND capture_source ~ '^[a-z0-9][a-z0-9._/-]{0,126}/v[1-9][0-9]*$' AND content_type ~ '^[^[:space:]/]+/[^[:space:]]+$' AND (content_encoding IS NULL OR content_encoding ~ '^[a-z0-9][a-z0-9._+-]{0,63}$')");
            table.HasCheckConstraint("ck_agent_run_log_stream_retention", "retention IN ('Ephemeral', 'Run', 'Team', 'Compliance', 'Permanent') AND (expires_at IS NULL OR expires_at > created_at) AND (retention <> 'Ephemeral' OR expires_at IS NOT NULL) AND (retention <> 'Permanent' OR expires_at IS NULL)");
            table.HasCheckConstraint("ck_agent_run_log_stream_state", "state IN ('Open', 'Completed', 'Truncated', 'Unavailable', 'Corrupt', 'CaptureFailed')");
            table.HasCheckConstraint("ck_agent_run_log_stream_terminal", "((state = 'Open' AND completed_at IS NULL AND error_code IS NULL) OR (state = 'Completed' AND completed_at IS NOT NULL AND error_code IS NULL) OR (state IN ('Truncated', 'Unavailable', 'Corrupt', 'CaptureFailed') AND completed_at IS NOT NULL AND error_code IS NOT NULL)) AND (state <> 'Completed' OR schema_version = 1 OR (content_digest_algorithm = 'Sha256' AND content_digest IS NOT NULL AND octet_length(content_digest) = 32))");
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

public sealed class AgentRunLogSegmentConfiguration : IEntityTypeConfiguration<AgentRunLogSegment>
{
    public void Configure(EntityTypeBuilder<AgentRunLogSegment> builder)
    {
        builder.ToTable("agent_run_log_segment", table =>
        {
            table.HasCheckConstraint("ck_agent_run_log_segment_bounds", "segment_ordinal > 0 AND start_offset_bytes >= 0 AND length_bytes > 0 AND worker_fence_epoch > 0 AND schema_version > 0");
            table.HasCheckConstraint("ck_agent_run_log_segment_identity", "capture_session_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_agent_run_log_segment_observation", "first_observed_at <= last_observed_at AND created_at >= last_observed_at");
        });
        builder.HasKey(segment => segment.Id);

        builder.HasOne(segment => segment.Stream).WithMany(stream => stream.Segments)
            .HasForeignKey(segment => new { segment.TeamId, segment.StreamId, segment.AgentRunId })
            .HasPrincipalKey(stream => new { stream.TeamId, stream.Id, stream.AgentRunId })
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
