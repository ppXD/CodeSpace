using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

/// <summary>
/// EF configuration for the <see cref="ProjectRepository"/> link table. Composite
/// primary key + filtered indexes mirror the SQL in migration
/// <c>0026_project_repository_link_table.sql</c> — keep the two in sync.
/// </summary>
public class ProjectRepositoryConfiguration : IEntityTypeConfiguration<ProjectRepository>
{
    public void Configure(EntityTypeBuilder<ProjectRepository> builder)
    {
        builder.ToTable("project_repository");
        builder.HasKey(pr => new { pr.ProjectId, pr.RepositoryId });

        builder.HasOne(pr => pr.Project)
            .WithMany()
            .HasForeignKey(pr => pr.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(pr => pr.Repository)
            .WithMany()
            .HasForeignKey(pr => pr.RepositoryId)
            .OnDelete(DeleteBehavior.Cascade);

        // Index hot paths. Filters match the partial indexes in the migration so
        // EF query plans line up with the runtime SQL.
        builder.HasIndex(pr => pr.RepositoryId).HasFilter("deleted_date IS NULL");
        builder.HasIndex(pr => pr.ProjectId).HasFilter("deleted_date IS NULL");
        builder.HasIndex(pr => pr.TeamId).HasFilter("deleted_date IS NULL");
    }
}
