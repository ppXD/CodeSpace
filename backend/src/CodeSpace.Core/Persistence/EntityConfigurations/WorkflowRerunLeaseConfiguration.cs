using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class WorkflowRerunLeaseConfiguration : IEntityTypeConfiguration<WorkflowRerunLease>
{
    public void Configure(EntityTypeBuilder<WorkflowRerunLease> builder)
    {
        builder.HasKey(l => l.Id);

        builder.Property(l => l.Status).HasMaxLength(16);

        // The UNIQUE PARTIAL index (original_run_id, map_node_id, branch_index) WHERE status = 'in_progress'
        // and both fork/original-run FKs (ON DELETE CASCADE) live in 0076_workflow_rerun_lease.sql — DbUp owns
        // the schema; the partial filter is Postgres-specific, so we don't double-track it via EF.
    }
}
