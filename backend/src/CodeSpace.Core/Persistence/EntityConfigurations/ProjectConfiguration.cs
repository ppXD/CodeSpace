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
        // Partial unique index expressed in SQL (migration 0022). EF cannot model the
        // `WHERE deleted_date IS NULL` filter via fluent API — the constraint exists at the
        // DB level; EF does not need to model it.
    }
}
