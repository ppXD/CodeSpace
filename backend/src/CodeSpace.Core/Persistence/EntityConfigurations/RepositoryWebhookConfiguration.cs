using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class RepositoryWebhookConfiguration : IEntityTypeConfiguration<RepositoryWebhook>
{
    public void Configure(EntityTypeBuilder<RepositoryWebhook> builder)
    {
        builder.HasKey(w => w.Id);

        builder.HasOne(w => w.Repository).WithMany().HasForeignKey(w => w.RepositoryId);

        builder.HasIndex(w => new { w.RepositoryId, w.ExternalId }).IsUnique();
    }
}
