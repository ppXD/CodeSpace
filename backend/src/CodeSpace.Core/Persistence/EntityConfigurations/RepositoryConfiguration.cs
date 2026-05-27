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

        // Phase 3.1 — Repository:Project is moving to N:M via project_repository (see
        // migration 0026 + IProjectRepositoryConfiguration). The legacy ProjectId column
        // is dual-written during the transition; the index stays so existing reads
        // through that column remain fast. A follow-up PR drops both the column and
        // this index once all readers consume the link table exclusively.
        builder.HasIndex(r => new { r.ProviderInstanceId, r.ExternalId }).IsUnique();
        builder.HasIndex(r => r.ProjectId);
    }
}
