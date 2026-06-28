using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class AgentRunConfiguration : IEntityTypeConfiguration<AgentRun>
{
    public void Configure(EntityTypeBuilder<AgentRun> builder)
    {
        builder.HasKey(r => r.Id);

        // Stored as its string name (matches WorkflowRun); 16 chars covers the longest value ("Succeeded"/"Cancelled").
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);

        // D4 — the owning workflow cell's iteration key (matches workflow_run_node.iteration_key: TEXT NOT NULL
        // DEFAULT ''). Empty for a top-level / standalone / non-container run.
        builder.Property(r => r.IterationKey).HasColumnName("iteration_key");

        builder.Property(r => r.TaskJson).HasColumnName("task_jsonb").HasColumnType("jsonb");
        builder.Property(r => r.ResultJson).HasColumnName("result_jsonb").HasColumnType("jsonb");

        // P3.1a — the harness-native CLI session/thread id, promoted from result_jsonb so the rerun CONTINUE lookup
        // is a column read. Nullable (a pre-session CLI / in-flight run has none).
        builder.Property(r => r.SessionId).HasColumnName("session_id");

        builder.Property(r => r.RunnerHandleJson).HasColumnName("runner_handle").HasColumnType("jsonb");
        builder.Property(r => r.FenceEpoch).HasColumnName("fence_epoch");
        builder.Property(r => r.LeaseExpiresAt).HasColumnName("lease_expires_at");
        builder.Property(r => r.ReattachAttempts).HasColumnName("reattach_attempts");

        // Npgsql xmin concurrency token — see WorkflowRunConfiguration for the rationale.
        builder.Property(r => r.Xmin)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();
    }
}
