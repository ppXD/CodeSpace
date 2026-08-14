using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class ProviderInstanceConfiguration : IEntityTypeConfiguration<ProviderInstance>
{
    public void Configure(EntityTypeBuilder<ProviderInstance> builder)
    {
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Provider).HasConversion<string>();

        // Text, like every other enum here: a psql reader sees the word, and reordering the C# enum
        // cannot silently repoint existing rows onto a different mode.
        builder.Property(p => p.WebhookScope).HasConversion<string>().HasMaxLength(32);

        builder.HasOne(p => p.Team).WithMany().HasForeignKey(p => p.TeamId);

        builder.HasIndex(p => new { p.TeamId, p.Provider, p.BaseUrl }).IsUnique();
    }
}
