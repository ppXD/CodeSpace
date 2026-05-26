using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.HasKey(p => p.Id);

        builder.HasOne(p => p.Team).WithMany().HasForeignKey(p => p.TeamId);

        builder.Property(p => p.Slug).HasMaxLength(64);
        builder.Property(p => p.Name).HasMaxLength(128);

        // Partial unique index is expressed in SQL (migration 0022) — EF's
        // HasIndex().HasFilter cannot fully replicate the migration's PostgreSQL filter
        // syntax. The constraint exists at the DB level; EF does not need to model it.
    }
}
