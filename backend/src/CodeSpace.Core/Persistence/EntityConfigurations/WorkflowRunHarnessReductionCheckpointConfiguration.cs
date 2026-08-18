using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public sealed class WorkflowRunHarnessReductionCheckpointConfiguration : IEntityTypeConfiguration<WorkflowRunHarnessReductionCheckpoint>
{
    public void Configure(EntityTypeBuilder<WorkflowRunHarnessReductionCheckpoint> builder)
    {
        builder.ToTable(WorkflowRunDataNames.HarnessReductionCheckpoint, table =>
        {
            table.HasCheckConstraint("ck_workflow_run_harness_reduction_checkpoint_bounds", "records_consumed >= 0 AND reducer_fence >= 0 AND revision > 0 AND contract_version > 0");
            table.HasCheckConstraint("ck_workflow_run_harness_reduction_checkpoint_claim", "(reducer_owner_id IS NULL AND reducer_lease_expires_at IS NULL) OR (reducer_owner_id IS NOT NULL AND reducer_fence > 0 AND reducer_lease_expires_at IS NOT NULL)");
            table.HasCheckConstraint("ck_workflow_run_harness_reduction_checkpoint_kind", "reducer_kind ~ '^[a-z0-9][a-z0-9._-]{0,62}/v[1-9][0-9]*$'");
            // IS NOT DISTINCT FROM on the 'streams' arm: a MISSING key makes jsonb_typeof() NULL, and a CHECK that
            // evaluates to NULL is SATISFIED, so an `= 'array'` arm would have admitted a position of '{}'.
            table.HasCheckConstraint("ck_workflow_run_harness_reduction_checkpoint_shape", "jsonb_typeof(position_jsonb) = 'object' AND jsonb_typeof(position_jsonb -> 'streams') IS NOT DISTINCT FROM 'array' AND jsonb_typeof(reduced_state_jsonb) = 'object'");
            table.HasCheckConstraint("ck_workflow_run_harness_reduction_checkpoint_time", "last_modified_at >= created_at");
        });
        builder.HasKey(checkpoint => checkpoint.Id);

        builder.Property(checkpoint => checkpoint.ReducerKind).HasMaxLength(80);
        builder.Property(checkpoint => checkpoint.PositionJson).HasColumnName("position_jsonb").HasColumnType("jsonb");
        builder.Property(checkpoint => checkpoint.ReducedStateJson).HasColumnName("reduced_state_jsonb").HasColumnType("jsonb");
        builder.Property(checkpoint => checkpoint.Xmin).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

        builder.HasOne<Team>().WithMany().HasForeignKey(checkpoint => checkpoint.TeamId).OnDelete(DeleteBehavior.Restrict);

        // Through the execution's own scope alternate key, so the denormalized Agent Run id is PROVED to be the one
        // that execution belongs to rather than trusted to have been stamped twice consistently.
        builder.HasOne(checkpoint => checkpoint.Execution).WithMany()
            .HasForeignKey(checkpoint => new { checkpoint.TeamId, checkpoint.ExecutionId, checkpoint.AgentRunId })
            .HasPrincipalKey(execution => new { execution.TeamId, execution.Id, execution.AgentRunId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(checkpoint => new { checkpoint.TeamId, checkpoint.ExecutionId, checkpoint.ReducerKind }).IsUnique()
            .HasDatabaseName("ux_workflow_run_harness_reduction_checkpoint_reducer");
        builder.HasIndex(checkpoint => new { checkpoint.TeamId, checkpoint.AgentRunId, checkpoint.LastModifiedAt, checkpoint.Id })
            .HasDatabaseName("ix_workflow_run_harness_reduction_checkpoint_agent_run");

        // Only a HELD lease can lapse, so the reaper's scan filters on holdership rather than on a non-null expiry.
        builder.HasIndex(checkpoint => new { checkpoint.ReducerLeaseExpiresAt, checkpoint.TeamId, checkpoint.Id })
            .HasDatabaseName("ix_workflow_run_harness_reduction_checkpoint_lease_expiry").HasFilter("reducer_owner_id IS NOT NULL");
    }
}
