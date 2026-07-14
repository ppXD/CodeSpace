using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class CompletionRequirementConfiguration : IEntityTypeConfiguration<CompletionRequirement>
{
    public void Configure(EntityTypeBuilder<CompletionRequirement> builder)
    {
        builder.HasKey(r => r.Id);
        builder.Property(r => r.EnvelopeJson).HasColumnName("envelope_jsonb").HasColumnType("jsonb");
        builder.HasIndex(r => new { r.WorkflowRunId, r.Kind, r.RequirementRef }).IsUnique();
    }
}

public class CompletionReceiptConfiguration : IEntityTypeConfiguration<CompletionReceipt>
{
    public void Configure(EntityTypeBuilder<CompletionReceipt> builder)
    {
        builder.HasKey(r => r.Id);
        builder.Property(r => r.EnvelopeJson).HasColumnName("envelope_jsonb").HasColumnType("jsonb");
        builder.HasIndex(r => new { r.WorkflowRunId, r.Kind, r.RequirementRef, r.AttemptId, r.TargetKey }).IsUnique();
    }
}
