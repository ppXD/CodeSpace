using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class CaptureIntentConfiguration : IEntityTypeConfiguration<CaptureIntent>
{
    public void Configure(EntityTypeBuilder<CaptureIntent> builder)
    {
        builder.HasKey(i => i.Id);

        // Stored as its string name (matches ToolCallLedger/AgentRun); 20 chars covers "Indeterminate" (13).
        builder.Property(i => i.Status).HasConversion<string>().HasMaxLength(20);

        builder.Property(i => i.ExpectationsJson).HasColumnName("expectations_jsonb").HasColumnType("jsonb");
        builder.Property(i => i.FactsJson).HasColumnName("facts_jsonb").HasColumnType("jsonb");

        // One promise per ATTEMPT — a reclaimed re-attach (bumped epoch) makes its own.
        builder.HasIndex(i => new { i.AgentRunId, i.FenceEpoch }).IsUnique();
    }
}
