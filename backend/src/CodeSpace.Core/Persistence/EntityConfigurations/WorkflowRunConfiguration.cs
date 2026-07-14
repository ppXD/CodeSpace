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
        builder.Property(r => r.CompletionEnforcementMode).HasMaxLength(16);
        builder.Property(r => r.OutputsJson).HasColumnName("outputs_jsonb").HasColumnType("jsonb");

        // run_kind is a Postgres GENERATED column (migration 0067) — the DB computes it from source_type, so EF must
        // never write it (INSERT/UPDATE) and must read it back after a write.
        builder.Property(r => r.RunKind).ValueGeneratedOnAddOrUpdate();

        // run_number is assigned by the trg_workflow_run_number BEFORE INSERT trigger (migration 0100) from the per-team
        // counter — EF must never write it and must read it back after insert. Same shape as run_kind above.
        builder.Property(r => r.RunNumber).ValueGeneratedOnAddOrUpdate();

        // Inline frozen definition for a SNAPSHOT run (dynamic-workflows substrate). NULL for an
        // authored run, which loads its definition from the pinned WorkflowVersion instead.
        builder.Property(r => r.DefinitionSnapshotJson).HasColumnName("definition_snapshot_jsonb").HasColumnType("jsonb");
        builder.Property(r => r.DefinitionSnapshotHash).HasColumnName("definition_snapshot_hash");

        // WorkflowId is nullable now (a snapshot run has no parent workflow), so the FK is optional.
        builder.HasOne(r => r.Workflow).WithMany().HasForeignKey(r => r.WorkflowId).IsRequired(false);

        builder.HasOne(r => r.RunRequest).WithMany().HasForeignKey(r => r.RunRequestId).IsRequired();

        // The WorkSession timeline index (migration 0070) — "the runs of session X, by turn", the access path the
        // continue + session-context-builder paths hit on every follow-up. Partial on session_id IS NOT NULL so it
        // stays tiny while session adoption is sparse; keep in sync with the migration.
        builder.HasIndex(r => new { r.SessionId, r.SessionTurnIndex }).HasFilter("session_id IS NOT NULL");

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
