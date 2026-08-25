using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public sealed class StorageDefaultConfiguration : IEntityTypeConfiguration<StorageDefault>
{
    public void Configure(EntityTypeBuilder<StorageDefault> builder)
    {
        builder.ToTable("storage_default", table =>
        {
            table.HasCheckConstraint("ck_storage_default_adoption_policy", "adoption_policy IN ('Automatic', 'Explicit')");
            table.HasCheckConstraint("ck_storage_default_config_object", "jsonb_typeof(config_jsonb) = 'object'");
            table.HasCheckConstraint("ck_storage_default_data_class_type_key", "data_class_type_key ~ '^[a-z0-9][a-z0-9.-]*/v[1-9][0-9]*$'");
            table.HasCheckConstraint("ck_storage_default_namespace_root", "btrim(namespace_root) <> '' AND namespace_root !~ '[[:cntrl:]]'");
            table.HasCheckConstraint("ck_storage_default_provider_type_key", "provider_type_key ~ '^[a-z0-9][a-z0-9.-]*/v[1-9][0-9]*$'");
            table.HasCheckConstraint("ck_storage_default_revision", "revision > 0");
        });

        builder.HasKey(value => value.Id);
        builder.Property(value => value.DataClassTypeKey).HasMaxLength(128);
        builder.Property(value => value.ProviderTypeKey).HasMaxLength(128);
        builder.Property(value => value.NonSecretConfigJson).HasColumnName("config_jsonb").HasColumnType("jsonb");
        builder.Property(value => value.NamespaceRoot).HasMaxLength(512);
        builder.Property(value => value.AdoptionPolicy).HasConversion<string>().HasMaxLength(16);
        builder.Property(value => value.Xmin)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        builder.HasOne(value => value.Credential).WithMany().HasForeignKey(value => value.CredentialId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(value => value.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(value => value.LastModifiedBy).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(value => value.DataClassTypeKey).IsUnique().HasDatabaseName("ux_storage_default_data_class");
    }
}

public sealed class StorageDefaultCredentialConfiguration : IEntityTypeConfiguration<StorageDefaultCredential>
{
    public void Configure(EntityTypeBuilder<StorageDefaultCredential> builder)
    {
        builder.ToTable("storage_default_credential", table =>
        {
            table.HasCheckConstraint("ck_storage_default_credential_encrypted_payload", "btrim(encrypted_payload) <> ''");
            table.HasCheckConstraint("ck_storage_default_credential_envelope_fingerprint", "envelope_fingerprint ~ '^sha256:[0-9a-f]{64}$'");
            table.HasCheckConstraint("ck_storage_default_credential_provider_type_key", "provider_type_key ~ '^[a-z0-9][a-z0-9.-]*/v[1-9][0-9]*$'");
            table.HasCheckConstraint("ck_storage_default_credential_safe_hint", "safe_hint IS NULL OR (char_length(safe_hint) BETWEEN 1 AND 32 AND btrim(safe_hint) <> '' AND safe_hint !~ '[[:cntrl:]]')");
        });

        builder.HasKey(value => value.Id);
        builder.Property(value => value.ProviderTypeKey).HasMaxLength(128);
        builder.Property(value => value.EncryptedPayload).HasColumnType("text");
        builder.Property(value => value.SafeHint).HasMaxLength(32);
        builder.Property(value => value.EnvelopeFingerprint).HasMaxLength(71);

        builder.HasOne<User>().WithMany().HasForeignKey(value => value.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(value => new { value.ProviderTypeKey, value.CreatedDate, value.Id }).HasDatabaseName("ix_storage_default_credential_provider_created");
    }
}

public sealed class StorageDefaultMaterializationConfiguration : IEntityTypeConfiguration<StorageDefaultMaterialization>
{
    public void Configure(EntityTypeBuilder<StorageDefaultMaterialization> builder)
    {
        builder.ToTable("storage_default_materialization", table =>
        {
            table.HasCheckConstraint("ck_storage_default_materialization_data_class_type_key", "data_class_type_key ~ '^[a-z0-9][a-z0-9.-]*/v[1-9][0-9]*$'");
            table.HasCheckConstraint("ck_storage_default_materialization_source_revision", "source_revision > 0");
        });

        builder.HasKey(value => value.Id);
        builder.Property(value => value.DataClassTypeKey).HasMaxLength(128);

        builder.HasOne(value => value.Team).WithMany().HasForeignKey(value => value.TeamId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(value => value.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<StorageProfile>()
            .WithMany()
            .HasForeignKey(value => new { value.TeamId, value.StorageProfileId })
            .HasPrincipalKey(profile => new { profile.TeamId, profile.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(value => new { value.TeamId, value.DataClassTypeKey }).IsUnique().HasDatabaseName("ux_storage_default_materialization_team_class");
        builder.HasIndex(value => new { value.DataClassTypeKey, value.CreatedDate, value.Id }).HasDatabaseName("ix_storage_default_materialization_class_created");
    }
}
