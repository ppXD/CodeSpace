using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class PackConfiguration : IEntityTypeConfiguration<Pack>
{
    public void Configure(EntityTypeBuilder<Pack> builder)
    {
        builder.HasKey(p => p.Id);

        // Stored as its string name (same convention as AgentDefinition.Origin); 16 chars covers "Github"/"GitUrl"/"Custom".
        builder.Property(p => p.Kind).HasConversion<string>().HasMaxLength(16);

        // Npgsql xmin concurrency token — see AgentDefinitionConfiguration for the rationale.
        builder.Property(p => p.Xmin)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();
    }
}
