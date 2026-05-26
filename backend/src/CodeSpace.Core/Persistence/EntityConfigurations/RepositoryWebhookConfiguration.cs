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

        // Store the enum as text so rows stay grep-able in psql + the value survives
        // C# enum reordering. Width matches the longest current variant ("DeadLettered" = 12)
        // plus headroom for any future state we add without a migration.
        builder.Property(w => w.RegistrationStatus).HasConversion<string>().HasMaxLength(32);

        // No HasIndex here for the (repository_id, external_id) unique — migration 0020
        // creates it as a partial unique on registration_status = 'Registered' which EF
        // can't express via fluent API. The index lives in SQL and is the production
        // invariant; EF doesn't need to model it for the entity to round-trip.
    }
}
