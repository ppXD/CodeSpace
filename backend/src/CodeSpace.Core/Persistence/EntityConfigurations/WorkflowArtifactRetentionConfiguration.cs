using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

/// <summary>
/// Column names and the table's own indexes come from the global <c>UseSnakeCaseNamingConvention()</c> and from
/// migration 0144; only the key, the two enum-as-string conversions, the lengths and the xmin concurrency token need
/// declaring. The CHECK constraints live in 0144 rather than here — they guard writes from any connection, not only
/// from EF.
/// </summary>
public sealed class WorkflowArtifactRetentionConfiguration : IEntityTypeConfiguration<WorkflowArtifactRetention>
{
    public void Configure(EntityTypeBuilder<WorkflowArtifactRetention> builder)
    {
        builder.ToTable("workflow_artifact_retention");
        builder.HasKey(row => row.ArtifactId);

        builder.Property(row => row.RetentionClass).HasConversion<string>().HasMaxLength(64);
        builder.Property(row => row.State).HasConversion<string>().HasMaxLength(32);
        builder.Property(row => row.HolderKind).HasMaxLength(64);
        builder.Property(row => row.LastErrorCode).HasMaxLength(128);
        builder.Property(row => row.LastErrorMessage).HasMaxLength(2048);
        builder.Property(row => row.Xmin).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

        builder.HasIndex(row => new { row.TeamId, row.NextSweepAt, row.ArtifactId }).HasDatabaseName("ix_workflow_artifact_retention_sweep")
            .HasFilter("state IN ('Declared', 'Quarantined')");
    }
}
