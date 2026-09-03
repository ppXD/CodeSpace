using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

/// <summary>Column names come from the global <c>UseSnakeCaseNamingConvention()</c>; only the table, the key, and the one-row-per-run uniqueness are declared here.</summary>
public class RunScorecardConfiguration : IEntityTypeConfiguration<RunScorecard>
{
    public void Configure(EntityTypeBuilder<RunScorecard> builder)
    {
        builder.ToTable("run_scorecard");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.ProjectionKind).HasMaxLength(60);
        builder.Property(r => r.EffortMode).HasMaxLength(20);
        builder.Property(r => r.LessonArm).HasMaxLength(16);
        builder.Property(r => r.BrainModel).HasMaxLength(200);
        builder.Property(r => r.ScorerVersion).HasMaxLength(60);
        // Match the migration's NUMERIC(18,6) exactly — an unspecified precision lets the provider pick its own
        // default, so the model and the schema would disagree about what a cent is.
        builder.Property(r => r.CostUsd).HasPrecision(18, 6);
        builder.Property(r => r.BrainPlaneUsd).HasPrecision(18, 6);
        builder.HasIndex(r => r.WorkflowRunId).IsUnique();
        builder.HasIndex(r => new { r.TeamId, r.CompletedAt });
    }
}
