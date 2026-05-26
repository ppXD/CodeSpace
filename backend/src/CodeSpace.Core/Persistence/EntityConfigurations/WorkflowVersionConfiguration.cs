using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class WorkflowVersionConfiguration : IEntityTypeConfiguration<WorkflowVersion>
{
    public void Configure(EntityTypeBuilder<WorkflowVersion> builder)
    {
        builder.HasKey(v => new { v.WorkflowId, v.Version });

        builder.Property(v => v.DefinitionJson).HasColumnName("definition_jsonb").HasColumnType("jsonb");

        builder.HasOne(v => v.Workflow).WithMany().HasForeignKey(v => v.WorkflowId);
    }
}
