using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

/// <summary>
/// EF configuration for <see cref="ConversationMember"/>. Composite PK link table; mirror
/// migration <c>0028_chat_foundation.sql</c>.
/// </summary>
public class ConversationMemberConfiguration : IEntityTypeConfiguration<ConversationMember>
{
    public void Configure(EntityTypeBuilder<ConversationMember> builder)
    {
        builder.ToTable("conversation_member");
        builder.HasKey(m => new { m.ConversationId, m.UserId });

        builder.Property(m => m.Role).HasConversion<string>();

        builder.HasOne(m => m.Conversation)
            .WithMany()
            .HasForeignKey(m => m.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        // "my conversations" — list every conversation a user belongs to.
        builder.HasIndex(m => m.UserId).HasFilter("deleted_date IS NULL");
        builder.HasIndex(m => m.TeamId).HasFilter("deleted_date IS NULL");
    }
}
