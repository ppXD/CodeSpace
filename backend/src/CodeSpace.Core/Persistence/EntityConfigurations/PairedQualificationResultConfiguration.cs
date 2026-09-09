using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public sealed class PairedQualificationResultConfiguration : IEntityTypeConfiguration<PairedQualificationResult>
{
    public void Configure(EntityTypeBuilder<PairedQualificationResult> builder)
    {
        builder.ToTable("paired_qualification_result");
        builder.HasKey(result => result.ObservationGroupId);
        builder.Property(result => result.ObservationGroupId).ValueGeneratedNever();
        builder.Property(result => result.ProtocolDigest).HasMaxLength(64);
        builder.Property(result => result.EvidenceDigest).HasMaxLength(64);
        builder.Property(result => result.ResultDigest).HasMaxLength(64);
        builder.Property(result => result.StatisticsVersion).HasMaxLength(80);
        builder.Property(result => result.OutcomeJson).HasColumnName("outcome_json").HasColumnType("text");
        builder.HasIndex(result => result.ProtocolDigest).IsUnique();
        builder.HasIndex(result => result.EvidenceDigest).IsUnique();
        builder.HasIndex(result => result.ResultDigest).IsUnique();
    }
}
