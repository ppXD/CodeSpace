using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

/// <summary>
/// EF configuration for <see cref="MessageReference"/> — the generic <c>@</c> reverse index.
/// Mirror migration <c>0028_chat_foundation.sql</c>.
/// </summary>
public class MessageReferenceConfiguration : IEntityTypeConfiguration<MessageReference>
{
    public void Configure(EntityTypeBuilder<MessageReference> builder)
    {
        builder.ToTable("message_reference");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.RefMetadataJson).HasColumnName("ref_metadata").HasColumnType("jsonb");

        builder.HasOne(r => r.Message)
            .WithMany()
            .HasForeignKey(r => r.MessageId)
            .OnDelete(DeleteBehavior.Cascade);

        // Forward: render a message's chips ("what does this message reference?").
        builder.HasIndex(r => r.MessageId);

        // Reverse: the generic backlink / mention-inbox path — "every message in this team
        // that references (ref_type, ref_id)". Leads with team_id so a tenant's query never
        // scans another team's references.
        builder.HasIndex(r => new { r.TeamId, r.RefType, r.RefId });
    }
}
