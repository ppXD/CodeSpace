using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.HasKey(m => m.Id);

        // Store enum as string so DB rows stay readable in psql / pgAdmin and survive enum reordering.
        builder.Property(m => m.Status).HasConversion<string>().HasMaxLength(32);

        // Payload column is jsonb in PG (migration), text in C# — handlers own (de)serialization.
        builder.Property(m => m.Payload).HasColumnType("jsonb");
    }
}
