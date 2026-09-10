using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public sealed class PairedQualificationProtocolConfiguration : IEntityTypeConfiguration<PairedQualificationProtocol>
{
    public void Configure(EntityTypeBuilder<PairedQualificationProtocol> builder)
    {
        builder.ToTable("paired_qualification_protocol");
        builder.HasKey(protocol => protocol.ObservationGroupId);
        builder.Property(protocol => protocol.ObservationGroupId).ValueGeneratedNever();
        builder.Property(protocol => protocol.CodeRevision).HasMaxLength(64);
        builder.Property(protocol => protocol.StatisticsVersion).HasMaxLength(80);
        builder.Property(protocol => protocol.Criterion).HasMaxLength(20);
        builder.Property(protocol => protocol.ControlSelectionJson).HasColumnType("jsonb");
        builder.Property(protocol => protocol.CandidateSelectionJson).HasColumnType("jsonb");
        builder.Property(protocol => protocol.RuntimeManifestJson).HasColumnType("jsonb");
        builder.Property(protocol => protocol.RuntimeManifestDigest).HasMaxLength(64);
        builder.Property(protocol => protocol.ProtocolDigest).HasMaxLength(64);
        builder.Property(protocol => protocol.MaxCostUsdPerLaunch).HasPrecision(18, 6);
        builder.HasIndex(protocol => new { protocol.TeamId, protocol.CreatedDate });
        builder.HasIndex(protocol => protocol.ProtocolDigest).IsUnique();
    }
}
