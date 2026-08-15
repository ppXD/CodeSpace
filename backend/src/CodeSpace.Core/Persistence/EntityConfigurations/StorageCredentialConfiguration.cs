using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public sealed class StorageCredentialConfiguration : IEntityTypeConfiguration<StorageCredential>
{
    public void Configure(EntityTypeBuilder<StorageCredential> builder)
    {
        builder.ToTable("storage_credential", table =>
        {
            table.HasCheckConstraint("ck_storage_credential_current_revision", "current_revision > 0");
            table.HasCheckConstraint("ck_storage_credential_revocation", "(state = 'Active' AND revoked_date IS NULL AND revoked_by IS NULL) OR (state = 'Revoked' AND revoked_date IS NOT NULL AND revoked_by IS NOT NULL)");
            table.HasCheckConstraint("ck_storage_credential_stable_name", "stable_name ~ '^[a-z0-9][a-z0-9-]{0,127}$'");
            table.HasCheckConstraint("ck_storage_credential_state", "state IN ('Active', 'Revoked')");
        });

        builder.HasKey(c => c.Id);
        builder.HasAlternateKey(c => new { c.TeamId, c.Id }).HasName("ak_storage_credential_team_id");
        builder.Property(c => c.StableName).HasMaxLength(128);
        builder.Property(c => c.State).HasConversion<string>().HasMaxLength(16);
        builder.Property(c => c.Xmin)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        builder.HasOne(c => c.Team).WithMany().HasForeignKey(c => c.TeamId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(c => c.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(c => c.RevokedBy).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(c => new { c.TeamId, c.StableName }).IsUnique().HasDatabaseName("ux_storage_credential_team_stable_name");
        builder.HasIndex(c => new { c.TeamId, c.State, c.StableName }).HasDatabaseName("ix_storage_credential_team_state");
    }
}

public sealed class StorageCredentialRevisionConfiguration : IEntityTypeConfiguration<StorageCredentialRevision>
{
    public void Configure(EntityTypeBuilder<StorageCredentialRevision> builder)
    {
        builder.ToTable("storage_credential_revision", table =>
        {
            table.HasCheckConstraint("ck_storage_credential_revision_encrypted_payload", "btrim(encrypted_payload) <> ''");
            table.HasCheckConstraint("ck_storage_credential_revision_envelope_fingerprint", "envelope_fingerprint ~ '^sha256:[0-9a-f]{64}$'");
            table.HasCheckConstraint("ck_storage_credential_revision_number", "revision > 0");
            table.HasCheckConstraint("ck_storage_credential_revision_provider_type_key", "provider_type_key ~ '^[a-z0-9][a-z0-9.-]*/v[1-9][0-9]*$'");
            table.HasCheckConstraint("ck_storage_credential_revision_safe_hint", "safe_hint IS NULL OR (char_length(safe_hint) BETWEEN 1 AND 32 AND btrim(safe_hint) <> '' AND safe_hint !~ '[[:cntrl:]]')");
        });

        builder.HasKey(r => r.Id);
        builder.Property(r => r.ProviderTypeKey).HasMaxLength(128);
        builder.Property(r => r.EncryptedPayload).HasColumnType("text");
        builder.Property(r => r.SafeHint).HasMaxLength(32);
        builder.Property(r => r.EnvelopeFingerprint).HasMaxLength(71);

        builder.HasOne(r => r.Credential)
            .WithMany(c => c.Revisions)
            .HasForeignKey(r => new { r.TeamId, r.StorageCredentialId })
            .HasPrincipalKey(c => new { c.TeamId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(r => r.CreatedBy).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(r => new { r.TeamId, r.StorageCredentialId, r.Revision }).IsUnique().HasDatabaseName("ux_storage_credential_revision_number");
        builder.HasIndex(r => new { r.TeamId, r.ProviderTypeKey, r.CreatedDate, r.Id }).HasDatabaseName("ix_storage_credential_revision_team_provider_created");
    }
}
