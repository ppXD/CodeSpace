using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class ConnectionWebhookConfiguration : IEntityTypeConfiguration<ConnectionWebhook>
{
    public void Configure(EntityTypeBuilder<ConnectionWebhook> builder)
    {
        builder.HasKey(w => w.Id);

        builder.HasOne(w => w.ProviderInstance).WithMany().HasForeignKey(w => w.ProviderInstanceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(w => w.Credential).WithMany().HasForeignKey(w => w.CredentialId).OnDelete(DeleteBehavior.Restrict);

        // Same conversion as RepositoryWebhook — the two tables share the vocabulary, so they
        // share how it is written down.
        builder.Property(w => w.RegistrationStatus).HasConversion<string>().HasMaxLength(32);

        // The (provider_instance_id, owner_path) uniqueness is partial on the non-terminal states
        // — DbUp 0121 owns it because EF's fluent API can't express the WHERE.
    }
}
