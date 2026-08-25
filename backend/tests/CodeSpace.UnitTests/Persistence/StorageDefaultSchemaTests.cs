using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Shouldly;

namespace CodeSpace.UnitTests.Persistence;

/// <summary>
/// Pins the deployment storage template's persistence shape. Nothing consumes a template yet — the materializer lane
/// is the intended reader — so the schema is the only contract that lane will have to work from.
/// </summary>
[Trait("Category", "Unit")]
public sealed class StorageDefaultSchemaTests
{
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused";

    /// <summary>
    /// The template is INSTANCE scoped: a team id would be meaningless on it and dangerous next to the SPA's ambient
    /// X-Team-Id header, so the entity must not grow one.
    /// </summary>
    [Fact]
    public void Template_is_instance_scoped_with_optimistic_concurrency()
    {
        using var db = BuildContext();
        var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(StorageDefault));

        entity.ShouldNotBeNull();
        entity.GetTableName().ShouldBe("storage_default");
        entity.GetProperties().Select(p => p.Name).Order().ShouldBe(new[]
        {
            "AdoptionPolicy", "CreatedBy", "CreatedDate", "CredentialId", "DataClassTypeKey", "Id", "IsEnabled",
            "LastModifiedBy", "LastModifiedDate", "NamespaceRoot", "NonSecretConfigJson", "ProviderTypeKey", "Revision", "Xmin",
        }.Order());
        entity.GetProperties().Select(p => p.Name).ShouldNotContain("TeamId");

        entity.FindProperty(nameof(StorageDefault.NamespaceRoot))!.GetMaxLength().ShouldBe(512);
        entity.FindProperty(nameof(StorageDefault.AdoptionPolicy))!.GetMaxLength().ShouldBe(16);
        entity.FindProperty(nameof(StorageDefault.NonSecretConfigJson))!.GetColumnName().ShouldBe("config_jsonb");
        entity.FindProperty(nameof(StorageDefault.Xmin))!.IsConcurrencyToken.ShouldBeTrue();

        var dataClass = Index(entity, "ux_storage_default_data_class");
        dataClass.IsUnique.ShouldBeTrue();
        dataClass.Properties.Select(p => p.Name).ShouldBe(new[] { "DataClassTypeKey" });
    }

    /// <summary>
    /// The adoption policy is a first-class CHECKed column, not a boolean: a data class added later must be forced to
    /// state how it is adopted, and an unknown value must never reach the row.
    /// </summary>
    [Fact]
    public void Adoption_policy_is_a_checked_vocabulary_column()
    {
        using var db = BuildContext();
        var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(StorageDefault));

        entity!.FindProperty(nameof(StorageDefault.AdoptionPolicy))!.ClrType.ShouldBe(typeof(StorageDefaultAdoptionPolicy));
        entity.GetCheckConstraints().Single(c => c.Name == "ck_storage_default_adoption_policy")
            .Sql.ShouldBe("adoption_policy IN ('Automatic', 'Explicit')");

        entity.GetCheckConstraints().Select(c => c.Name).ShouldBe(new[]
        {
            "ck_storage_default_adoption_policy",
            "ck_storage_default_config_object",
            "ck_storage_default_data_class_type_key",
            "ck_storage_default_namespace_root",
            "ck_storage_default_provider_type_key",
            "ck_storage_default_revision",
        }, ignoreOrder: true);
    }

    /// <summary>Team-less ciphertext, with no plaintext slot and no last-modified fields — the envelope is append-only.</summary>
    [Fact]
    public void Credential_is_team_less_ciphertext_with_no_plaintext_slot()
    {
        using var db = BuildContext();
        var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(StorageDefaultCredential));

        entity.ShouldNotBeNull();
        entity.GetTableName().ShouldBe("storage_default_credential");
        entity.GetProperties().Select(p => p.Name).Order().ShouldBe(new[]
        {
            "CreatedBy", "CreatedDate", "EncryptedPayload", "EnvelopeFingerprint", "Id", "ProviderTypeKey", "SafeHint",
        }.Order());
        entity.GetProperties().Select(p => p.Name).ShouldNotContain("TeamId");
        entity.GetProperties().Select(p => p.Name).ShouldNotContain("LastModifiedDate");
    }

    /// <summary>
    /// The provenance ledger the materializer will fill. Its unique key is (team, data class) because for an Explicit
    /// class the presence of that row IS the team's adoption; and its profile reference is tenant-bound so a
    /// materialization can never name another team's profile.
    /// </summary>
    [Fact]
    public void Materialization_is_keyed_by_team_and_data_class_and_binds_a_same_team_profile()
    {
        using var db = BuildContext();
        var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(StorageDefaultMaterialization));

        entity.ShouldNotBeNull();
        entity.GetTableName().ShouldBe("storage_default_materialization");

        var adoption = Index(entity, "ux_storage_default_materialization_team_class");
        adoption.IsUnique.ShouldBeTrue();
        adoption.Properties.Select(p => p.Name).ShouldBe(new[] { "TeamId", "DataClassTypeKey" });

        var profile = entity.GetForeignKeys().Single(f => f.PrincipalEntityType.ClrType == typeof(StorageProfile));
        profile.Properties.Select(p => p.Name).ShouldBe(new[] { "TeamId", "StorageProfileId" });
        profile.PrincipalKey.Properties.Select(p => p.Name).ShouldBe(new[] { "TeamId", "Id" });
        profile.DeleteBehavior.ShouldBe(DeleteBehavior.Restrict);
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
