using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class AgentRunEventConfiguration : IEntityTypeConfiguration<AgentRunEvent>
{
    public void Configure(EntityTypeBuilder<AgentRunEvent> builder)
    {
        builder.HasKey(e => e.Id);

        // BIGSERIAL on the DB side; value-generated-on-add so the SaveChanges round-trip returns the
        // assigned sequence (matches WorkflowRunRecord).
        builder.Property(e => e.Sequence).HasColumnName("sequence").ValueGeneratedOnAdd();

        // Closed normalized vocabulary, stored as its string name (matches AgentRunStatus). 32 covers the longest ("ApprovalRequested").
        builder.Property(e => e.Kind).HasConversion<string>().HasMaxLength(32);

        builder.Property(e => e.DataJson).HasColumnName("data_json").HasColumnType("jsonb");

        // D2 #1: ref to the offloaded structured payload when data_json was too large to keep inline.
        builder.Property(e => e.DataArtifactId).HasColumnName("data_artifact_id");

        builder.HasOne(e => e.Run).WithMany().HasForeignKey(e => e.AgentRunId);
    }
}
