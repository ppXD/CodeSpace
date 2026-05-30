using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class WorkflowRunWaitConfiguration : IEntityTypeConfiguration<WorkflowRunWait>
{
    public void Configure(EntityTypeBuilder<WorkflowRunWait> builder)
    {
        builder.HasKey(w => w.Id);

        builder.Property(w => w.WaitKind).HasMaxLength(16);
        builder.Property(w => w.Status).HasMaxLength(16);
        builder.Property(w => w.Token).HasMaxLength(128);
        builder.Property(w => w.NodeId).HasMaxLength(128);
        builder.Property(w => w.IterationKey).HasMaxLength(128);
        builder.Property(w => w.PayloadJson).HasColumnName("payload_jsonb").HasColumnType("jsonb");

        builder.HasIndex(w => new { w.RunId, w.NodeId, w.IterationKey }).IsUnique();
    }
}
