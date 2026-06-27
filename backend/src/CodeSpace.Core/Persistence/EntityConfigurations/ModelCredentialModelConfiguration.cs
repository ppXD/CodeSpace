using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

/// <summary>
/// EF configuration for <see cref="ModelCredentialModel"/> — the credential's child model list. Mirrors the SQL
/// in migration <c>0059_model_credential_model.sql</c> (keep the two in sync). Snake-case column names come from
/// the global <c>UseSnakeCaseNamingConvention()</c>, so only the FK, the cascade, the enum conversion, and the
/// per-credential unique index need declaring.
/// </summary>
public class ModelCredentialModelConfiguration : IEntityTypeConfiguration<ModelCredentialModel>
{
    public void Configure(EntityTypeBuilder<ModelCredentialModel> builder)
    {
        builder.HasKey(m => m.Id);

        // Stored as its string name (matches every other enum in the schema, incl. CredentialStatus).
        builder.Property(m => m.Source).HasConversion<string>();

        // The cached capability tier is likewise stored as its string name (null = not yet tiered).
        builder.Property(m => m.CapabilityTier).HasConversion<string>();

        // The objectively-probed tier for an opaque id (a SEPARATE column from the brain verdict above).
        builder.Property(m => m.ProbedCapabilityTier).HasConversion<string>();

        builder.HasOne(m => m.Credential)
            .WithMany(c => c.Models)
            .HasForeignKey(m => m.ModelCredentialId)
            .OnDelete(DeleteBehavior.Cascade);

        // One row per (credential, model id): makes a reflection refresh idempotent and keeps a manual model
        // from colliding with a reflected one. PER-credential, not global — the same model id can be backed by
        // two different credentials (e.g. two teams' keys, or a direct key + a gateway).
        builder.HasIndex(m => new { m.ModelCredentialId, m.ModelId }).IsUnique();
    }
}
