using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Shouldly;

namespace CodeSpace.UnitTests.Persistence;

/// <summary>
/// Pins the team-level, provider-neutral routing ledger. Routes decide where a data class is written; the resulting
/// artifact location always records the exact profile revision and never consults mutable routing state while reading.
/// </summary>
[Trait("Category", "Unit")]
public sealed class StorageRouteSchemaTests
{
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused";

    [Fact]
    public void Route_is_team_scoped_versioned_and_not_falsely_workflow_owned()
    {
        using var db = BuildContext();
        var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(StorageRoute));

        entity.ShouldNotBeNull();
        entity.GetTableName().ShouldBe("storage_route");
        entity.GetTableName()!.ShouldNotStartWith("workflow_run_");
        entity.GetProperties().Select(p => p.Name).Order().ShouldBe(new[]
        {
            "CreatedBy", "CreatedDate", "CurrentRevision", "DataClassTypeKey", "Id", "LastModifiedBy",
            "LastModifiedDate", "State", "TeamId", "Xmin",
        }.Order());

        entity.FindProperty(nameof(StorageRoute.DataClassTypeKey))!.GetMaxLength().ShouldBe(128);
        entity.FindProperty(nameof(StorageRoute.State))!.GetMaxLength().ShouldBe(16);
        entity.FindProperty(nameof(StorageRoute.Xmin))!.IsConcurrencyToken.ShouldBeTrue();

        var dataClass = Index(entity, "ux_storage_route_team_data_class");
        dataClass.IsUnique.ShouldBeTrue();
        dataClass.Properties.Select(p => p.Name).ShouldBe(new[] { "TeamId", "DataClassTypeKey" });

        entity.GetCheckConstraints().Select(c => c.Name).ShouldBe(new[]
        {
            "ck_storage_route_current_revision",
            "ck_storage_route_data_class_type_key",
            "ck_storage_route_state",
        }, ignoreOrder: true);
    }

    [Fact]
    public void Route_revision_is_append_only_and_targets_current_at_write_or_an_exact_profile_revision()
    {
        using var db = BuildContext();
        var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(StorageRouteRevision));

        entity.ShouldNotBeNull();
        entity.GetTableName().ShouldBe("storage_route_revision");
        entity.GetTableName()!.ShouldNotStartWith("workflow_run_");
        entity.GetProperties().Select(p => p.Name).Order().ShouldBe(new[]
        {
            "CreatedBy", "CreatedDate", "Id", "PinnedProfileRevision", "ProfileRevisionMode", "Revision",
            "StorageProfileId", "StorageRouteId", "TeamId",
        }.Order());

        entity.FindProperty(nameof(StorageRouteRevision.ProfileRevisionMode))!.GetMaxLength().ShouldBe(24);

        var revision = Index(entity, "ux_storage_route_revision_number");
        revision.IsUnique.ShouldBeTrue();
        revision.Properties.Select(p => p.Name).ShouldBe(new[] { "TeamId", "StorageRouteId", "Revision" });

        var route = entity.GetForeignKeys().Single(f => f.PrincipalEntityType.ClrType == typeof(StorageRoute));
        route.Properties.Select(p => p.Name).ShouldBe(new[] { "TeamId", "StorageRouteId" });
        route.PrincipalKey.Properties.Select(p => p.Name).ShouldBe(new[] { "TeamId", "Id" });
        route.DeleteBehavior.ShouldBe(DeleteBehavior.Restrict);

        var profile = entity.GetForeignKeys().Single(f => f.PrincipalEntityType.ClrType == typeof(StorageProfile));
        profile.Properties.Select(p => p.Name).ShouldBe(new[] { "TeamId", "StorageProfileId" });
        profile.PrincipalKey.Properties.Select(p => p.Name).ShouldBe(new[] { "TeamId", "Id" });
        profile.DeleteBehavior.ShouldBe(DeleteBehavior.Restrict);

        entity.GetCheckConstraints().Select(c => c.Name).ShouldBe(new[]
        {
            "ck_storage_route_revision_number",
            "ck_storage_route_revision_profile_selection",
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
