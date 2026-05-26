using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class CredentialConfiguration : IEntityTypeConfiguration<Credential>
{
    public void Configure(EntityTypeBuilder<Credential> builder)
    {
        builder.HasKey(c => c.Id);

        builder.Property(c => c.AuthType).HasConversion<string>();
        builder.Property(c => c.Status).HasConversion<string>();

        builder.HasOne(c => c.Team).WithMany().HasForeignKey(c => c.TeamId);
        builder.HasOne(c => c.ProviderInstance).WithMany().HasForeignKey(c => c.ProviderInstanceId);
        builder.HasOne(c => c.Owner).WithMany().HasForeignKey(c => c.OwnerUserId);
    }
}
