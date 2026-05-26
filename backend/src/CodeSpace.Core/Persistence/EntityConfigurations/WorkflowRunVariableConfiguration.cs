using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class WorkflowRunVariableConfiguration : IEntityTypeConfiguration<WorkflowRunVariable>
{
    public void Configure(EntityTypeBuilder<WorkflowRunVariable> builder)
    {
        builder.HasKey(v => v.Id);

        // FK with cascade delete: when a workflow_run is hard-deleted (rare — usually we
        // soft-delete via status), every snapshot row goes with it. The DB-level cascade
        // matches the SQL migration's ON DELETE CASCADE.
        builder.HasOne(v => v.Run).WithMany().HasForeignKey(v => v.RunId);

        builder.Property(v => v.Scope).HasMaxLength(16);
        builder.Property(v => v.ValueType).HasMaxLength(32);

        // The unique (run_id, scope, name) index + the value-column CHECK constraint are
        // expressed in the migration (Postgres-specific syntax). DbUp owns the schema; we
        // don't double-track via EF HasIndex.
    }
}
