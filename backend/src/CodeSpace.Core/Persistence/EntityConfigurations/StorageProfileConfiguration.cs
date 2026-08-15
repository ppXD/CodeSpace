using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public sealed class StorageProfileConfiguration : IEntityTypeConfiguration<StorageProfile>
{
    public void Configure(EntityTypeBuilder<StorageProfile> builder)
    {
        builder.ToTable("storage_profile", table =>
        {
            table.HasCheckConstraint("ck_storage_profile_current_revision", "current_revision > 0");
            table.HasCheckConstraint("ck_storage_profile_stable_name", "stable_name ~ '^[a-z0-9][a-z0-9-]{0,127}$'");
            table.HasCheckConstraint("ck_storage_profile_state", "state IN ('Draft', 'Active', 'Disabled', 'Retired')");
        });

        builder.HasKey(p => p.Id);
        builder.HasAlternateKey(p => new { p.TeamId, p.Id }).HasName("ak_storage_profile_team_id");
        builder.Property(p => p.StableName).HasMaxLength(128);
        builder.Property(p => p.State).HasConversion<string>().HasMaxLength(16);
        builder.Property(p => p.Xmin)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        builder.HasOne(p => p.Team).WithMany().HasForeignKey(p => p.TeamId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(p => new { p.TeamId, p.StableName }).IsUnique().HasDatabaseName("ux_storage_profile_team_stable_name");
    }
}

public sealed class StorageProfileRevisionConfiguration : IEntityTypeConfiguration<StorageProfileRevision>
{
    public void Configure(EntityTypeBuilder<StorageProfileRevision> builder)
    {
        builder.ToTable("storage_profile_revision", table =>
        {
            table.HasCheckConstraint("ck_storage_profile_revision_config_object", "jsonb_typeof(config_jsonb) = 'object'");
            table.HasCheckConstraint("ck_storage_profile_revision_credential_ref", "credential_ref IS NULL OR btrim(credential_ref) <> ''");
            table.HasCheckConstraint("ck_storage_profile_revision_namespace_fingerprint", "namespace_fingerprint ~ '^sha256:[0-9a-f]{64}$'");
            table.HasCheckConstraint("ck_storage_profile_revision_number", "revision > 0");
            table.HasCheckConstraint("ck_storage_profile_revision_provider_type_key", "provider_type_key ~ '^[a-z0-9][a-z0-9.-]*/v[1-9][0-9]*$'");
        });

        builder.HasKey(r => r.Id);
        builder.Property(r => r.ProviderTypeKey).HasMaxLength(128);
        builder.Property(r => r.NonSecretConfigJson).HasColumnName("config_jsonb").HasColumnType("jsonb");
        builder.Property(r => r.CredentialRef).HasMaxLength(512);
        builder.Property(r => r.NamespaceFingerprint).HasMaxLength(71);

        builder.HasOne(r => r.Profile)
            .WithMany(p => p.Revisions)
            .HasForeignKey(r => new { r.TeamId, r.StorageProfileId })
            .HasPrincipalKey(p => new { p.TeamId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(r => new { r.TeamId, r.StorageProfileId, r.Revision }).IsUnique().HasDatabaseName("ux_storage_profile_revision_number");
        builder.HasIndex(r => new { r.TeamId, r.ProviderTypeKey, r.CreatedDate, r.Id }).HasDatabaseName("ix_storage_profile_revision_team_provider_created");
    }
}
