using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class BudgetReservationConfiguration : IEntityTypeConfiguration<BudgetReservation>
{
    public void Configure(EntityTypeBuilder<BudgetReservation> builder)
    {
        builder.HasKey(r => r.Id);
        builder.Property(r => r.ReservedUsd).HasColumnType("numeric");
        builder.Property(r => r.SettledUsd).HasColumnType("numeric");
        builder.Property(r => r.CapUsd).HasColumnType("numeric");
        builder.HasIndex(r => new { r.WorkflowRunId, r.Kind, r.ScopeKey }).IsUnique();
    }
}
