using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class SupervisorDecisionRecordConfiguration : IEntityTypeConfiguration<SupervisorDecisionRecord>
{
    public void Configure(EntityTypeBuilder<SupervisorDecisionRecord> builder)
    {
        // The entity is named …Record (it's the ledger ROW, distinct from a future model-facing decision DTO), but the
        // table is supervisor_decision — map it explicitly so EF's name convention doesn't default to
        // supervisor_decision_record (which the migration never creates).
        builder.ToTable("supervisor_decision");

        builder.HasKey(d => d.Id);

        // Stored as its string name (matches ToolCallLedger / AgentRun); 20 chars covers the longest value ("AwaitingApproval" is 16).
        builder.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);

        builder.Property(d => d.InputHash).HasMaxLength(64);
        builder.Property(d => d.IdempotencyKey).HasMaxLength(200);
        builder.Property(d => d.DecisionKind).HasColumnName("decision_kind");
        builder.Property(d => d.SupervisorRunId).HasColumnName("supervisor_run_id");
        builder.Property(d => d.PayloadJson).HasColumnName("payload_jsonb").HasColumnType("jsonb");
        builder.Property(d => d.OutcomeJson).HasColumnName("outcome_jsonb").HasColumnType("jsonb");
        builder.Property(d => d.FenceEpoch).HasColumnName("fence_epoch");

        // BIGSERIAL on the DB side; value-generated-on-add so the SaveChanges round-trip returns the actual sequence
        // number (mirrors WorkflowRunRecord.Sequence).
        builder.Property(d => d.Sequence).HasColumnName("sequence").ValueGeneratedOnAdd();

        // Observation-only axes. Neither has a column default: 0161's trigger allocates only after taking the exact-run
        // transaction lock. StoryOrder never changes; ObservationRevision is regenerated on every accepted update.
        var storyOrder = builder.Property(d => d.StoryOrder).HasColumnName("story_order").ValueGeneratedOnAdd();
        storyOrder.Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        storyOrder.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        var observationRevision = builder.Property(d => d.ObservationRevision).HasColumnName("observation_revision").ValueGeneratedOnAddOrUpdate();
        observationRevision.Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        observationRevision.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
        builder.HasIndex(d => new { d.SupervisorRunId, d.StoryOrder }).IsUnique().HasDatabaseName("ux_supervisor_decision_run_story_order");
        builder.HasIndex(d => new { d.SupervisorRunId, d.ObservationRevision }).IsUnique().HasDatabaseName("ux_supervisor_decision_run_observation_revision");

        // The exactly-once invariant: one row per (run, idempotency key). A racing duplicate INSERT hits this and the
        // loser reads the winner's row (the dedup path) — see SupervisorDecisionLog.TryClaimAsync.
        builder.HasIndex(d => new { d.SupervisorRunId, d.IdempotencyKey }).IsUnique();

        // Npgsql xmin concurrency token — same convention as ToolCallLedger.
        builder.Property(d => d.Xmin)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();
    }
}
