using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Shouldly;

namespace CodeSpace.UnitTests.Persistence;

/// <summary>
/// Pins the provider-neutral, team-scoped storage profile ledger before any runtime path reads it. A profile is the
/// stable identity/current pointer; provider configuration is append-only history. Secret values have no persistence
/// property in either entity: only an opaque credential reference may cross this boundary.
/// </summary>
[Trait("Category", "Unit")]
public sealed class StorageProfileSchemaTests
{
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused";

    [Fact]
    public void Profile_is_a_global_team_scoped_identity_with_optimistic_concurrency()
    {
        using var db = BuildContext();
        var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(StorageProfile));

        entity.ShouldNotBeNull();
        entity.GetTableName().ShouldBe("storage_profile");
        entity.GetTableName()!.ShouldNotStartWith("workflow_run_");
        entity.GetProperties().Select(p => p.Name).Order().ShouldBe(new[]
        {
            "CreatedBy", "CreatedDate", "CurrentRevision", "Id", "LastModifiedBy", "LastModifiedDate",
            "StableName", "State", "TeamId", "Xmin",
        }.Order());

        entity.FindProperty(nameof(StorageProfile.StableName))!.GetMaxLength().ShouldBe(128);
        entity.FindProperty(nameof(StorageProfile.State))!.GetMaxLength().ShouldBe(16);
        entity.FindProperty(nameof(StorageProfile.Xmin))!.IsConcurrencyToken.ShouldBeTrue();
        entity.FindProperty(nameof(StorageProfile.Xmin))!.GetColumnName().ShouldBe("xmin");

        var stableName = Index(entity, "ux_storage_profile_team_stable_name");
        stableName.IsUnique.ShouldBeTrue();
        stableName.Properties.Select(p => p.Name).ShouldBe(new[] { "TeamId", "StableName" });

        entity.GetCheckConstraints().Select(c => c.Name).ShouldBe(new[]
        {
            "ck_storage_profile_current_revision",
            "ck_storage_profile_stable_name",
            "ck_storage_profile_state",
        }, ignoreOrder: true);
    }

    [Fact]
    public void Revision_is_append_only_provider_neutral_and_contains_no_plaintext_secret_slot()
    {
        using var db = BuildContext();
        var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(StorageProfileRevision));

        entity.ShouldNotBeNull();
        entity.GetTableName().ShouldBe("storage_profile_revision");
        entity.GetTableName()!.ShouldNotStartWith("workflow_run_");
        entity.GetProperties().Select(p => p.Name).Order().ShouldBe(new[]
        {
            "CreatedBy", "CreatedDate", "CredentialRef", "Id", "NamespaceFingerprint", "NonSecretConfigJson",
            "ProviderTypeKey", "Revision", "StorageProfileId", "TeamId",
        }.Order());

        entity.FindProperty(nameof(StorageProfileRevision.NonSecretConfigJson))!.GetColumnName().ShouldBe("config_jsonb");
        entity.FindProperty(nameof(StorageProfileRevision.NonSecretConfigJson))!.GetColumnType().ShouldBe("jsonb");
        entity.FindProperty(nameof(StorageProfileRevision.ProviderTypeKey))!.GetMaxLength().ShouldBe(128);
        entity.FindProperty(nameof(StorageProfileRevision.CredentialRef))!.GetMaxLength().ShouldBe(512);
        entity.FindProperty(nameof(StorageProfileRevision.NamespaceFingerprint))!.GetMaxLength().ShouldBe(71);

        entity.GetProperties().Select(p => p.Name).ShouldNotContain(name =>
            name.Contains("ApiKey", StringComparison.OrdinalIgnoreCase)
            || name.Contains("AccessKey", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Secret", StringComparison.OrdinalIgnoreCase) && name != nameof(StorageProfileRevision.NonSecretConfigJson));

        var revision = Index(entity, "ux_storage_profile_revision_number");
        revision.IsUnique.ShouldBeTrue();
        revision.Properties.Select(p => p.Name).ShouldBe(new[] { "TeamId", "StorageProfileId", "Revision" });

        var profile = entity.GetForeignKeys().Single(f => f.PrincipalEntityType.ClrType == typeof(StorageProfile));
        profile.Properties.Select(p => p.Name).ShouldBe(new[] { "TeamId", "StorageProfileId" });
        profile.PrincipalKey.Properties.Select(p => p.Name).ShouldBe(new[] { "TeamId", "Id" });
        profile.DeleteBehavior.ShouldBe(DeleteBehavior.Restrict);

        entity.GetCheckConstraints().Select(c => c.Name).ShouldBe(new[]
        {
            "ck_storage_profile_revision_config_object",
            "ck_storage_profile_revision_credential_ref",
            "ck_storage_profile_revision_namespace_fingerprint",
            "ck_storage_profile_revision_number",
            "ck_storage_profile_revision_provider_type_key",
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
