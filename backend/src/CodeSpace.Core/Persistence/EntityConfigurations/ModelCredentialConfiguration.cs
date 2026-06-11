using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class ModelCredentialConfiguration : IEntityTypeConfiguration<ModelCredential>
{
    public void Configure(EntityTypeBuilder<ModelCredential> builder)
    {
        builder.HasKey(c => c.Id);

        // Stored as its string name (matches CredentialStatus on the git Credential).
        builder.Property(c => c.Status).HasConversion<string>();

        builder.HasOne(c => c.Team).WithMany().HasForeignKey(c => c.TeamId);
    }
}
