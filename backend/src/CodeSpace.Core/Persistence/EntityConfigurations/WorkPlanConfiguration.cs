using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class WorkPlanConfiguration : IEntityTypeConfiguration<WorkPlan>
{
    public void Configure(EntityTypeBuilder<WorkPlan> builder)
    {
        builder.HasKey(p => p.Id);

        builder.Property(p => p.ItemsJson).HasColumnType("jsonb");
        builder.Property(p => p.SuccessCriteriaJson).HasColumnType("jsonb");
        builder.Property(p => p.RisksJson).HasColumnType("jsonb");
        builder.Property(p => p.AssumptionsJson).HasColumnType("jsonb");
        builder.Property(p => p.QuestionsJson).HasColumnType("jsonb");

        builder.HasOne(p => p.Team).WithMany().HasForeignKey(p => p.TeamId);

        // Versions are contiguous per run; the unique pair is what the store's insert race resolves against.
        builder.HasIndex(p => new { p.WorkflowRunId, p.Version }).IsUnique();

        // Exactly-once per (run, origin key) — partial: only rows that carry a key participate.
        builder.HasIndex(p => new { p.WorkflowRunId, p.OriginKey }).IsUnique().HasFilter("origin_key IS NOT NULL");

        builder.HasIndex(p => p.TeamId);
    }
}
