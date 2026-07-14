using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class BudgetReservationConfiguration : IEntityTypeConfiguration<BudgetReservation>
{
    public void Configure(EntityTypeBuilder<BudgetReservation> builder)
    {
        builder.HasKey(r => r.Id);
        builder.Property(r => r.ReservedUsd).HasPrecision(12, 4);
        builder.Property(r => r.SettledUsd).HasPrecision(12, 4);
        builder.HasIndex(r => new { r.WorkflowRunId, r.Kind, r.ScopeKey }).IsUnique();
    }
}
