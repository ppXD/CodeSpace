using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

/// <summary>Column names come from the global <c>UseSnakeCaseNamingConvention()</c>; only the table, the team-id key, and the numeric amount are declared here.</summary>
public sealed class BudgetTeamCapConfiguration : IEntityTypeConfiguration<BudgetTeamCap>
{
    public void Configure(EntityTypeBuilder<BudgetTeamCap> builder)
    {
        builder.ToTable("budget_team_cap");
        builder.HasKey(cap => cap.TeamId);
        builder.Property(cap => cap.TeamId).ValueGeneratedNever();
        builder.Property(cap => cap.CapUsd).HasColumnType("numeric");
    }
}
