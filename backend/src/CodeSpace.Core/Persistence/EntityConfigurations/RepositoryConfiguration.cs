using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class RepositoryConfiguration : IEntityTypeConfiguration<Repository>
{
    public void Configure(EntityTypeBuilder<Repository> builder)
    {
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Visibility).HasConversion<string>();
        builder.Property(r => r.Status).HasConversion<string>();

        builder.HasOne(r => r.Team).WithMany().HasForeignKey(r => r.TeamId);
        builder.HasOne(r => r.ProviderInstance).WithMany().HasForeignKey(r => r.ProviderInstanceId);
        builder.HasOne(r => r.Credential).WithMany().HasForeignKey(r => r.CredentialId);

        // Phase 3.0 — repositories live inside Projects (TeamCity-style VcsRoot). The FK is
        // enforced at DB level (migration 0022 backfills NULL → team's "default" project).
        // No nav-property is exposed because the project-detail page reads repos via the
        // RepositoryService project-filter; backing the join through a nav would cost an
        // extra Include on every list query.
        builder.HasIndex(r => new { r.ProviderInstanceId, r.ExternalId }).IsUnique();
        builder.HasIndex(r => r.ProjectId);
    }
}
