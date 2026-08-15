using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class WorkflowRunRecordConfiguration : IEntityTypeConfiguration<WorkflowRunRecord>
{
    public void Configure(EntityTypeBuilder<WorkflowRunRecord> builder)
    {
        builder.HasKey(r => r.Id);
        // BIGSERIAL on the DB side; EF treats it as a value-generated-on-add column so the
        // SaveChanges round-trip returns the actual sequence number.
        builder.Property(r => r.Sequence)
            .HasColumnName("sequence")
            .ValueGeneratedOnAdd();

        builder.Property(r => r.PayloadJson).HasColumnName("payload_json").HasColumnType("jsonb");

        builder.HasOne(r => r.Run).WithMany().HasForeignKey(r => r.RunId);

        // Self-FK for hierarchical records (attempt → parent node row). EF needs the
        // navigation explicitly even though we don't expose it on the entity.
        builder.HasOne<WorkflowRunRecord>().WithMany().HasForeignKey(r => r.ParentRecordId);

        builder.HasIndex(r => new { r.RunId, r.CorrelationId, r.Sequence }).HasDatabaseName("ix_workflow_run_record_interaction_correlation")
            .HasFilter("correlation_id IS NOT NULL AND record_type IN ('interaction.started', 'interaction.completed', 'interaction.failed', 'interaction.delta')");
        builder.HasIndex(r => new { r.RecordType, r.OccurredAt, r.Id }).HasDatabaseName("ix_workflow_run_record_model_call_candidates")
            .HasFilter("correlation_id IS NOT NULL AND record_type IN ('interaction.completed', 'interaction.failed')");
    }
}
