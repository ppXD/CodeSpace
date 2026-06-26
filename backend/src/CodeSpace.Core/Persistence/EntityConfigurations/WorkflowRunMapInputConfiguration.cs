using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class WorkflowRunMapInputConfiguration : IEntityTypeConfiguration<WorkflowRunMapInput>
{
    public void Configure(EntityTypeBuilder<WorkflowRunMapInput> builder)
    {
        builder.HasKey(s => s.Id);

        // FK with cascade delete: a hard-deleted workflow_run takes its map-input snapshots with it,
        // matching the SQL migration's ON DELETE CASCADE.
        builder.HasOne(s => s.Run).WithMany().HasForeignKey(s => s.RunId);

        builder.Property(s => s.Sensitivity).HasMaxLength(16);
        builder.Property(s => s.ContentHash).HasMaxLength(64);

        // The unique (run_id, map_node_id, iteration_key) index + the SecretDerived value CHECK constraint
        // live in the migration (Postgres-specific syntax). DbUp owns the schema; we don't double-track via EF.
    }
}
