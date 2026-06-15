using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class WorkflowRunConfiguration : IEntityTypeConfiguration<WorkflowRun>
{
    public void Configure(EntityTypeBuilder<WorkflowRun> builder)
    {
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(r => r.OutputsJson).HasColumnName("outputs_jsonb").HasColumnType("jsonb");

        // Inline frozen definition for a SNAPSHOT run (dynamic-workflows substrate). NULL for an
        // authored run, which loads its definition from the pinned WorkflowVersion instead.
        builder.Property(r => r.DefinitionSnapshotJson).HasColumnName("definition_snapshot_jsonb").HasColumnType("jsonb");
        builder.Property(r => r.DefinitionSnapshotHash).HasColumnName("definition_snapshot_hash");

        // WorkflowId is nullable now (a snapshot run has no parent workflow), so the FK is optional.
        builder.HasOne(r => r.Workflow).WithMany().HasForeignKey(r => r.WorkflowId).IsRequired(false);

        builder.HasOne(r => r.RunRequest).WithMany().HasForeignKey(r => r.RunRequestId).IsRequired();

        // Npgsql xmin concurrency token. EF appends WHERE xmin = $loaded to every UPDATE; second
        // writer racing the same run gets DbUpdateConcurrencyException and the engine skips.
        // PostgreSQL stamps xmin automatically on every INSERT/UPDATE, so we map it via
        // HasColumnName + ValueGeneratedOnAddOrUpdate + IsConcurrencyToken. The Xmin property on
        // the entity is `uint` per Npgsql's documented convention.
        builder.Property(r => r.Xmin)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();
    }
}
