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
        builder.Property(r => r.PublishMode).HasConversion<string>();

        builder.HasOne(r => r.Team).WithMany().HasForeignKey(r => r.TeamId);
        builder.HasOne(r => r.ProviderInstance).WithMany().HasForeignKey(r => r.ProviderInstanceId);
        builder.HasOne(r => r.Credential).WithMany().HasForeignKey(r => r.CredentialId);

        builder.HasIndex(r => new { r.ProviderInstanceId, r.ExternalId }).IsUnique();
    }
}
