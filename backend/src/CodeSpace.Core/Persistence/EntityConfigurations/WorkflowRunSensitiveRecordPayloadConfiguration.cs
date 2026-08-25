using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public sealed class WorkflowRunSensitiveRecordPayloadConfiguration : IEntityTypeConfiguration<WorkflowRunSensitiveRecordPayload>
{
    public void Configure(EntityTypeBuilder<WorkflowRunSensitiveRecordPayload> builder)
    {
        builder.ToTable("workflow_run_sensitive_record_payload");
        builder.HasKey(payload => payload.RecordId);
        builder.Property(payload => payload.RecordId).HasColumnName("record_id");
        builder.Property(payload => payload.RunId).HasColumnName("run_id");
        builder.Property(payload => payload.TeamId).HasColumnName("team_id");
        builder.Property(payload => payload.PayloadKind).HasColumnName("payload_kind").HasMaxLength(64);
        builder.Property(payload => payload.Ciphertext).HasColumnName("ciphertext");
        builder.Property(payload => payload.CiphertextArtifactId).HasColumnName("ciphertext_artifact_id");
        builder.Property(payload => payload.CiphertextSizeBytes).HasColumnName("ciphertext_size_bytes");
        builder.Property(payload => payload.CreatedAt).HasColumnName("created_at");
        builder.HasOne<WorkflowRunRecord>().WithOne().HasForeignKey<WorkflowRunSensitiveRecordPayload>(payload => payload.RecordId);
        builder.HasOne<WorkflowRun>().WithMany().HasForeignKey(payload => payload.RunId);
        builder.HasOne<Team>().WithMany().HasForeignKey(payload => payload.TeamId);
        builder.HasOne<WorkflowArtifact>().WithMany().HasForeignKey(payload => payload.CiphertextArtifactId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(payload => payload.CiphertextArtifactId).HasFilter("ciphertext_artifact_id IS NOT NULL");
    }
}
