using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public sealed class AgentRunLogVerificationConfiguration : IEntityTypeConfiguration<AgentRunLogVerification>
{
    public void Configure(EntityTypeBuilder<AgentRunLogVerification> builder)
    {
        builder.ToTable("agent_run_log_verification");
        builder.HasKey(value => value.Id);
        builder.HasIndex(value => new { value.TeamId, value.StreamId, value.StreamRevision }).IsUnique().HasDatabaseName("ux_agent_run_log_verification_head");
        builder.HasOne<AgentRunLogStream>().WithMany().HasForeignKey(value => new { value.TeamId, value.StreamId, value.AgentRunId })
            .HasPrincipalKey(value => new { value.TeamId, value.Id, value.AgentRunId }).OnDelete(DeleteBehavior.Restrict);
        builder.Property(value => value.Accumulator).HasColumnType("bytea");
        builder.Property(value => value.ManifestDigest).HasColumnType("bytea");
        builder.Property(value => value.Xmin).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
    }
}
