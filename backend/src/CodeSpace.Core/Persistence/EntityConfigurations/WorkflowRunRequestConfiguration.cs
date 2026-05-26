using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class WorkflowRunRequestConfiguration : IEntityTypeConfiguration<WorkflowRunRequest>
{
    public void Configure(EntityTypeBuilder<WorkflowRunRequest> builder)
    {
        builder.HasKey(r => r.Id);

        // Status persisted as TEXT — keeps the door open for new states (Throttled, etc.)
        // without an enum migration. Length is generous (16) to fit any reasonable label.
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);

        builder.Property(r => r.ActivationSnapshotJson).HasColumnName("activation_snapshot_json").HasColumnType("jsonb");
        builder.Property(r => r.RawHeadersRedactedJson).HasColumnName("raw_headers_redacted_json").HasColumnType("jsonb");
        builder.Property(r => r.NormalizedPayloadJson).HasColumnName("normalized_payload_json").HasColumnType("jsonb");
        builder.Property(r => r.RequestMetadataJson).HasColumnName("request_metadata_json").HasColumnType("jsonb");
        builder.Property(r => r.VerificationResultJson).HasColumnName("verification_result_json").HasColumnType("jsonb");
    }
}
