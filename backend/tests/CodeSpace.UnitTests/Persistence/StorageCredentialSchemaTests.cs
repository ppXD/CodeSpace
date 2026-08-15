using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Shouldly;

namespace CodeSpace.UnitTests.Persistence;

/// <summary>
/// Pins the additive provider-neutral storage credential ledger before any runtime writer or reader exists. The only
/// provider payload slot is the self-contained encrypted envelope produced by the shared payload-encryption primitive;
/// Data Protection carries its key id in that envelope and resolves algorithms through the shared key ring, so parallel
/// key-version or algorithm columns would be a second, drift-prone source of truth.
/// </summary>
[Trait("Category", "Unit")]
public sealed class StorageCredentialSchemaTests
{
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused";

    [Fact]
    public void Credential_is_a_global_team_scoped_identity_with_terminal_revocation_and_concurrency()
    {
        using var db = BuildContext();
        var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(StorageCredential));

        entity.ShouldNotBeNull();
        entity.GetTableName().ShouldBe("storage_credential");
        entity.GetTableName()!.ShouldNotStartWith("workflow_run_");
        entity.GetProperties().Select(p => p.Name).Order().ShouldBe(new[]
        {
            "CreatedBy", "CreatedDate", "CurrentRevision", "Id", "RevokedBy", "RevokedDate", "StableName", "State",
            "TeamId", "Xmin",
        }.Order());

        entity.FindProperty(nameof(StorageCredential.StableName))!.GetMaxLength().ShouldBe(128);
        entity.FindProperty(nameof(StorageCredential.State))!.GetMaxLength().ShouldBe(16);
        entity.FindProperty(nameof(StorageCredential.Xmin))!.IsConcurrencyToken.ShouldBeTrue();
        entity.FindProperty(nameof(StorageCredential.Xmin))!.GetColumnName().ShouldBe("xmin");

        var tenantIdentity = entity.GetKeys().Single(k => k.Properties.Select(p => p.Name).SequenceEqual(new[] { "TeamId", "Id" }));
        tenantIdentity.GetName().ShouldBe("ak_storage_credential_team_id");

        var stableName = Index(entity, "ux_storage_credential_team_stable_name");
        stableName.IsUnique.ShouldBeTrue();
        stableName.Properties.Select(p => p.Name).ShouldBe(new[] { "TeamId", "StableName" });

        entity.GetForeignKeys().Where(f => f.PrincipalEntityType.ClrType == typeof(User))
            .Select(f => f.Properties.ShouldHaveSingleItem().Name)
            .ShouldBe(new[] { "CreatedBy", "RevokedBy" }, ignoreOrder: true);

        entity.GetCheckConstraints().Select(c => c.Name).ShouldBe(new[]
        {
            "ck_storage_credential_current_revision",
            "ck_storage_credential_revocation",
            "ck_storage_credential_stable_name",
            "ck_storage_credential_state",
        }, ignoreOrder: true);
    }

    [Fact]
    public void Revision_is_append_only_provider_neutral_and_has_only_an_encrypted_payload_slot()
    {
        using var db = BuildContext();
        var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(StorageCredentialRevision));

        entity.ShouldNotBeNull();
        entity.GetTableName().ShouldBe("storage_credential_revision");
        entity.GetTableName()!.ShouldNotStartWith("workflow_run_");
        entity.GetProperties().Select(p => p.Name).Order().ShouldBe(new[]
        {
            "CreatedBy", "CreatedDate", "EncryptedPayload", "EnvelopeFingerprint", "Id", "ProviderTypeKey", "Revision",
            "SafeHint", "StorageCredentialId", "TeamId",
        }.Order());

        entity.FindProperty(nameof(StorageCredentialRevision.EncryptedPayload))!.GetColumnName().ShouldBe("encrypted_payload");
        entity.FindProperty(nameof(StorageCredentialRevision.EncryptedPayload))!.GetColumnType().ShouldBe("text");
        entity.FindProperty(nameof(StorageCredentialRevision.ProviderTypeKey))!.GetMaxLength().ShouldBe(128);
        entity.FindProperty(nameof(StorageCredentialRevision.SafeHint))!.GetMaxLength().ShouldBe(32);
        entity.FindProperty(nameof(StorageCredentialRevision.EnvelopeFingerprint))!.GetMaxLength().ShouldBe(71);

        entity.GetProperties().Select(p => p.Name).ShouldNotContain(name =>
            name.Contains("Plain", StringComparison.OrdinalIgnoreCase)
            || name.Contains("ApiKey", StringComparison.OrdinalIgnoreCase)
            || name.Contains("AccessKey", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Secret", StringComparison.OrdinalIgnoreCase)
            || name.Contains("KeyVersion", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Algorithm", StringComparison.OrdinalIgnoreCase));

        var revision = Index(entity, "ux_storage_credential_revision_number");
        revision.IsUnique.ShouldBeTrue();
        revision.Properties.Select(p => p.Name).ShouldBe(new[] { "TeamId", "StorageCredentialId", "Revision" });

        var credential = entity.GetForeignKeys().Single(f => f.PrincipalEntityType.ClrType == typeof(StorageCredential));
        credential.Properties.Select(p => p.Name).ShouldBe(new[] { "TeamId", "StorageCredentialId" });
        credential.PrincipalKey.Properties.Select(p => p.Name).ShouldBe(new[] { "TeamId", "Id" });
        credential.DeleteBehavior.ShouldBe(DeleteBehavior.Restrict);
        entity.GetForeignKeys().Single(f => f.PrincipalEntityType.ClrType == typeof(User))
            .Properties.ShouldHaveSingleItem().Name.ShouldBe("CreatedBy");

        entity.GetCheckConstraints().Select(c => c.Name).ShouldBe(new[]
        {
            "ck_storage_credential_revision_encrypted_payload",
            "ck_storage_credential_revision_envelope_fingerprint",
            "ck_storage_credential_revision_number",
            "ck_storage_credential_revision_provider_type_key",
            "ck_storage_credential_revision_safe_hint",
        }, ignoreOrder: true);
    }

    private static CodeSpaceDbContext BuildContext()
    {
        var options = new DbContextOptionsBuilder<CodeSpaceDbContext>()
            .UseNpgsql(UnreachableDatabase)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new CodeSpaceDbContext(options);
    }

    private static IIndex Index(IEntityType entity, string name) => entity.GetIndexes().Single(i => i.GetDatabaseName() == name);
}
