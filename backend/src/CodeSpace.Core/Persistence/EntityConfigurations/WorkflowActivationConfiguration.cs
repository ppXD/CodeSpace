using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class WorkflowActivationConfiguration : IEntityTypeConfiguration<WorkflowActivation>
{
    public void Configure(EntityTypeBuilder<WorkflowActivation> builder)
    {
        builder.HasKey(a => a.Id);

        builder.Property(a => a.ConfigJson).HasColumnName("config_jsonb").HasColumnType("jsonb");

        builder.HasOne(a => a.Workflow).WithMany().HasForeignKey(a => a.WorkflowId);
    }
}
