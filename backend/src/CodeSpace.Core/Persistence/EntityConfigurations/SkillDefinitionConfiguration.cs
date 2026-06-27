using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class SkillDefinitionConfiguration : IEntityTypeConfiguration<SkillDefinition>
{
    public void Configure(EntityTypeBuilder<SkillDefinition> builder)
    {
        builder.HasKey(s => s.Id);

        // Stored as its string name (matches AgentDefinition.Origin); 16 chars covers "Authored"/"Imported".
        builder.Property(s => s.Origin).HasConversion<string>().HasMaxLength(16);

        // Verbatim frontmatter kept as a string (same convention as AgentDefinition.RawFrontmatterJson).
        builder.Property(s => s.RawFrontmatterJson).HasColumnName("raw_frontmatter_jsonb").HasColumnType("jsonb");

        // Npgsql xmin concurrency token — see AgentDefinitionConfiguration for the rationale.
        builder.Property(s => s.Xmin)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();
    }
}
