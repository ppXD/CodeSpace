using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class VariableConfiguration : IEntityTypeConfiguration<Variable>
{
    public void Configure(EntityTypeBuilder<Variable> builder)
    {
        builder.HasKey(v => v.Id);

        // Store enums as strings (matches the VARCHAR(16/32) columns in the migration).
        // Numeric int would force lockstep between enum value ordering + the DB — strings
        // are forward-compatible: adding a new enum value (Project, Organization, ...) is
        // a no-op at the DB level.
        builder.Property(v => v.Scope).HasConversion<string>().HasMaxLength(16);
        builder.Property(v => v.ValueType).HasConversion<string>().HasMaxLength(32);

        // TeamId FK — owning team is always known even for Workflow scope (denormalised).
        builder.HasOne(v => v.Team).WithMany().HasForeignKey(v => v.TeamId);

        // The partial-unique index (scope, scope_id, name) WHERE deleted_date IS NULL is
        // expressed in the migration (PostgreSQL-specific). DbUp owns the schema; we don't
        // re-declare it via EF HasIndex.
    }
}
