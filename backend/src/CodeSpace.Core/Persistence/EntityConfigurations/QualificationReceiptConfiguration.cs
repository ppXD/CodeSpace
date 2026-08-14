using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class QualificationReceiptConfiguration : IEntityTypeConfiguration<QualificationReceipt>
{
    public void Configure(EntityTypeBuilder<QualificationReceipt> builder)
    {
        builder.HasKey(r => r.Id);

        builder.Property(r => r.GrantedPerformance).HasConversion<string>().HasMaxLength(20);

        builder.Property(r => r.VerifierBundleJson).HasColumnName("verifier_bundle_jsonb").HasColumnType("jsonb");
        builder.Property(r => r.CohortJson).HasColumnName("cohort_jsonb").HasColumnType("jsonb");
        builder.Property(r => r.MetricsJson).HasColumnName("metrics_jsonb").HasColumnType("jsonb");

        builder.HasIndex(r => new { r.Mode, r.CapabilityKey, r.ExpiresAt });
    }
}
