using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class TeamConfiguration : IEntityTypeConfiguration<Team>
{
    public void Configure(EntityTypeBuilder<Team> builder)
    {
        builder.HasKey(t => t.Id);

        builder.HasOne(t => t.Owner).WithMany().HasForeignKey(t => t.OwnerUserId);

        // Kind stored as text — easier to inspect via psql than an int enum, matches the
        // CHECK constraint added in migration 0008. EF Core's HasConversion<string>() handles
        // the round-trip automatically; reads convert back to the enum value.
        builder.Property(t => t.Kind).HasConversion<string>();
    }
}
