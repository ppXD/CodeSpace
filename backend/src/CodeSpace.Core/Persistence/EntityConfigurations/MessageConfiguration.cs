using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

/// <summary>
/// EF configuration for <see cref="Message"/>. The cursor-pagination index
/// <c>(conversation_id, id)</c> is the hot path; the generated <c>search_tsv</c> column +
/// its GIN index live in migration <c>0028_chat_foundation.sql</c> (EF can't model a
/// generated tsvector column, so it's declared SQL-side and ignored here).
/// </summary>
public class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.ToTable("message");
        builder.HasKey(m => m.Id);

        builder.HasOne(m => m.Conversation)
            .WithMany()
            .HasForeignKey(m => m.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Chronological pagination within a conversation. UUID v7 ids sort by creation, so
        // this single composite index serves "latest N", "before id X", "after id X" — the
        // entire read pattern of an infinite-scroll message pane + SignalR backfill.
        builder.HasIndex(m => new { m.ConversationId, m.Id });

        // The generated search_tsv tsvector column + its GIN index are declared SQL-side
        // in migration 0028. There's no CLR property for it on Message, so EF never maps
        // it — no Ignore() needed (and Ignore would throw, the property doesn't exist).
    }
}
