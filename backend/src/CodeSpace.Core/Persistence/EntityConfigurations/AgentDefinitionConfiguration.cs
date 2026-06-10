using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class AgentDefinitionConfiguration : IEntityTypeConfiguration<AgentDefinition>
{
    public void Configure(EntityTypeBuilder<AgentDefinition> builder)
    {
        builder.HasKey(a => a.Id);

        // Stored as its string name (matches AgentRunStatus etc.); 16 chars covers "Authored"/"Imported".
        builder.Property(a => a.Origin).HasConversion<string>().HasMaxLength(16);

        // jsonb blobs kept as strings (same convention as AgentRun.TaskJson) — modelled by the service layer later.
        builder.Property(a => a.ToolsJson).HasColumnName("tools_jsonb").HasColumnType("jsonb");
        builder.Property(a => a.SkillsJson).HasColumnName("skills_jsonb").HasColumnType("jsonb");
        builder.Property(a => a.McpServersJson).HasColumnName("mcp_servers_jsonb").HasColumnType("jsonb");
        builder.Property(a => a.RawFrontmatterJson).HasColumnName("raw_frontmatter_jsonb").HasColumnType("jsonb");

        // Npgsql xmin concurrency token — see AgentRunConfiguration / WorkflowRunConfiguration for the rationale.
        builder.Property(a => a.Xmin)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();
    }
}
