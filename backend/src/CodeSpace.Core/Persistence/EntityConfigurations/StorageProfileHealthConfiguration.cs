using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class StorageProfileHealthConfiguration : IEntityTypeConfiguration<StorageProfileHealth>
{
    public void Configure(EntityTypeBuilder<StorageProfileHealth> builder)
    {
        builder.ToTable("storage_profile_health");
        builder.HasKey(health => new { health.TeamId, health.StorageProfileId });
        builder.Property(health => health.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(health => health.FailureStage).HasConversion<string>().HasMaxLength(32);
        builder.Property(health => health.FailureCode).HasConversion<string>().HasMaxLength(64);

        builder.HasOne(health => health.Profile)
            .WithOne()
            .HasForeignKey<StorageProfileHealth>(health => new { health.TeamId, health.StorageProfileId })
            .HasPrincipalKey<StorageProfile>(profile => new { profile.TeamId, profile.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
