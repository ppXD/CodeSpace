using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

/// <summary>Column names come from the global <c>UseSnakeCaseNamingConvention()</c>; the two CHECKs mirror migration 0235 exactly, and the unique index is an expression there (over both nullable target columns) so it is declared in SQL only.</summary>
public sealed class PairedQualificationResultPinConfiguration : IEntityTypeConfiguration<PairedQualificationResultPin>
{
    public void Configure(EntityTypeBuilder<PairedQualificationResultPin> builder)
    {
        builder.ToTable("paired_qualification_result_pin", table =>
        {
            table.HasCheckConstraint("ck_paired_qualification_result_pin_kind", "kind IN ('LogStream', 'Artifact', 'CleanupReceipt', 'AgentRun')");
            table.HasCheckConstraint("ck_paired_qualification_result_pin_target", "num_nonnulls(pinned_id, pinned_artifact_id) = 1 AND (kind = 'Artifact') = (pinned_artifact_id IS NOT NULL)");
        });
        builder.HasKey(pin => pin.Id);
        builder.Property(pin => pin.Id).ValueGeneratedNever();
        builder.Property(pin => pin.Kind).HasConversion<string>().HasMaxLength(32);
        builder.Ignore(pin => pin.Target);

        builder.HasOne<PairedQualificationResult>().WithMany()
            .HasForeignKey(pin => pin.ResultId)
            .HasPrincipalKey(result => result.ObservationGroupId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
