using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class ToolCallLedgerConfiguration : IEntityTypeConfiguration<ToolCallLedger>
{
    public void Configure(EntityTypeBuilder<ToolCallLedger> builder)
    {
        builder.HasKey(l => l.Id);

        // Stored as its string name (matches AgentRun); 20 chars covers the longest value ("AwaitingApproval" is 16).
        builder.Property(l => l.Status).HasConversion<string>().HasMaxLength(20);

        builder.Property(l => l.InputHash).HasMaxLength(64);
        builder.Property(l => l.IdempotencyKey).HasMaxLength(200);
        builder.Property(l => l.ResultJson).HasColumnName("result_jsonb").HasColumnType("jsonb");
        builder.Property(l => l.DecisionEnvelopeJson).HasColumnName("decision_envelope_jsonb").HasColumnType("jsonb");
        builder.Property(l => l.ApprovalMessageId).HasColumnName("approval_message_id");
        builder.Property(l => l.ApprovalToken).HasColumnName("approval_token");
        builder.Property(l => l.ApprovalDeadlineAt).HasColumnName("approval_deadline_at");
        builder.Property(l => l.ApprovedByUserId).HasColumnName("approved_by_user_id");
        builder.Property(l => l.ApprovedAt).HasColumnName("approved_at");
        builder.Property(l => l.FenceEpoch).HasColumnName("fence_epoch");

        var admissionOrdinal = builder.Property(l => l.AdmissionOrdinal).HasColumnName("admission_ordinal").ValueGeneratedOnAdd();
        admissionOrdinal.Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        admissionOrdinal.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);

        // The exactly-once invariant: one row per (run, idempotency key). A racing duplicate INSERT hits this and
        // the loser reads the winner's row (the dedup path) — see ToolCallLedgerService.TryClaimAsync.
        builder.HasIndex(l => new { l.AgentRunId, l.IdempotencyKey }).IsUnique();

        // One database-owned source rank per AgentRun. The same partial btree makes MAX(admission_ordinal) for the
        // allocator an index-shaped lookup; legacy NULL rows remain honest and outside both uniqueness + projection.
        builder.HasIndex(l => new { l.AgentRunId, l.AdmissionOrdinal })
            .HasDatabaseName("ux_tool_call_ledger_run_admission_ordinal")
            .HasFilter("admission_ordinal IS NOT NULL")
            .IsUnique();

        // Npgsql xmin concurrency token — see WorkflowRunConfiguration for the rationale.
        builder.Property(l => l.Xmin)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();
    }
}
