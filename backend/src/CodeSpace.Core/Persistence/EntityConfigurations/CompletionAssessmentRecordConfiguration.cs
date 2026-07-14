using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class CompletionAssessmentRecordConfiguration : IEntityTypeConfiguration<CompletionAssessmentRecord>
{
    public void Configure(EntityTypeBuilder<CompletionAssessmentRecord> builder)
    {
        builder.ToTable("completion_assessment");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.AssessmentJson).HasColumnName("assessment_jsonb").HasColumnType("jsonb");
        builder.HasIndex(r => new { r.WorkflowRunId, r.CreatedDate });
    }
}
