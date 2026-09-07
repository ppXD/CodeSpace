using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public sealed class AgentRunLogVerificationConfiguration : IEntityTypeConfiguration<AgentRunLogVerification>
{
    public void Configure(EntityTypeBuilder<AgentRunLogVerification> builder)
    {
        builder.ToTable("agent_run_log_verification", table =>
        {
            table.HasCheckConstraint("ck_agent_run_log_verification_bounds", "worker_fence_epoch > 0 AND stream_revision > 0 AND segment_count >= 0 AND total_bytes >= 0 AND source_offset_bytes >= 0 AND next_segment_ordinal > 0 AND next_segment_ordinal <= segment_count + 1 AND verified_bytes >= 0 AND verified_bytes <= total_bytes AND revision > 0 AND octet_length(accumulator) = 32 AND last_modified_at >= created_at AND (sealed_at IS NULL OR sealed_at <= last_modified_at)");
            table.HasCheckConstraint("ck_agent_run_log_verification_claim", "(recovery_intent_id IS NULL AND recovery_owner_id IS NULL AND recovery_fence_epoch IS NULL) OR (recovery_intent_id IS NOT NULL AND recovery_owner_id IS NOT NULL AND recovery_fence_epoch > 0)");
            table.HasCheckConstraint("ck_agent_run_log_verification_seal", "(sealed_at IS NULL AND manifest_digest IS NULL) OR (sealed_at IS NOT NULL AND manifest_digest IS NOT NULL AND octet_length(manifest_digest) = 32 AND next_segment_ordinal = segment_count + 1 AND verified_bytes = total_bytes)");
        });
        builder.HasKey(value => value.Id);
        builder.HasIndex(value => new { value.TeamId, value.StreamId, value.StreamRevision }).IsUnique().HasDatabaseName("ux_agent_run_log_verification_head");
        builder.HasOne<AgentRunLogStream>().WithMany().HasForeignKey(value => new { value.TeamId, value.StreamId, value.AgentRunId })
            .HasPrincipalKey(value => new { value.TeamId, value.Id, value.AgentRunId }).OnDelete(DeleteBehavior.Restrict);
        builder.Property(value => value.Accumulator).HasColumnType("bytea");
        builder.Property(value => value.ManifestDigest).HasColumnType("bytea");
        builder.Property(value => value.Xmin).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
    }
}
