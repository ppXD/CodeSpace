using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

/// <summary>
/// EF configuration for <see cref="Conversation"/>. Keep the indexes in sync with
/// migration <c>0028_chat_foundation.sql</c> — both are the source of truth for the
/// same physical schema (EF for query-plan shape, the SQL for the actual DDL DbUp runs).
/// </summary>
public class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> builder)
    {
        builder.ToTable("conversation");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Kind).HasConversion<string>();
        builder.Property(c => c.Visibility).HasConversion<string>();

        builder.HasOne(c => c.Team).WithMany().HasForeignKey(c => c.TeamId);

        // Channel slug is unique per team while present + alive. DM / group leave slug null,
        // which the partial filter excludes (NULLs don't collide in the unique index).
        builder.HasIndex(c => new { c.TeamId, c.Slug })
            .IsUnique()
            .HasFilter("slug IS NOT NULL AND deleted_date IS NULL");

        builder.HasIndex(c => c.TeamId).HasFilter("deleted_date IS NULL");

        // DM singleton key — see migration 0029. Direct only; channel/group leave it null,
        // which the partial filter excludes so they never collide.
        builder.HasIndex(c => new { c.TeamId, c.DmKey })
            .IsUnique()
            .HasFilter("dm_key IS NOT NULL AND deleted_date IS NULL");
    }
}
