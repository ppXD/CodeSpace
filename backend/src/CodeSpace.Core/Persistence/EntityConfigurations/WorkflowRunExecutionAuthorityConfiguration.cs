using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public sealed class WorkflowRunExecutionAuthorityConfiguration : IEntityTypeConfiguration<WorkflowRunExecutionAuthority>
{
    public void Configure(EntityTypeBuilder<WorkflowRunExecutionAuthority> builder)
    {
        builder.HasKey(r => r.WorkflowRunId);
        builder.Property(r => r.ReceiptJson).HasColumnType("jsonb");
        builder.HasOne<WorkflowRun>().WithOne().HasForeignKey<WorkflowRunExecutionAuthority>(r => new { r.TeamId, r.WorkflowRunId }).HasPrincipalKey<WorkflowRun>(r => new { r.TeamId, r.Id });
    }
}
