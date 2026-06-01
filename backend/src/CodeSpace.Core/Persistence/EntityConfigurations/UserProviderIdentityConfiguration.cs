using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

/// <summary>
/// EF configuration for <see cref="UserProviderIdentity"/>. Keep the unique index in sync with
/// migration <c>0038_user_provider_identity.sql</c> — both describe the same physical schema
/// (EF for query-plan shape, the SQL for the DDL DbUp runs). The UserId FK lives only in the
/// migration (REFERENCES app_user): no User navigation here, so resolver queries never entangle
/// with the global bot query filter on User.
/// </summary>
public class UserProviderIdentityConfiguration : IEntityTypeConfiguration<UserProviderIdentity>
{
    public void Configure(EntityTypeBuilder<UserProviderIdentity> builder)
    {
        builder.ToTable("user_provider_identity");
        builder.HasKey(i => i.Id);

        builder.HasOne(i => i.ProviderInstance).WithMany().HasForeignKey(i => i.ProviderInstanceId);
        builder.HasOne(i => i.Credential).WithMany().HasForeignKey(i => i.CredentialId);

        // One LIVE acting identity per (user, provider instance). Unlink = soft-delete, which the
        // partial filter excludes so the user can re-link without colliding (mirrors conversation slug).
        builder.HasIndex(i => new { i.UserId, i.ProviderInstanceId })
            .IsUnique()
            .HasFilter("deleted_date IS NULL");
    }
}
