using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Shouldly;

namespace CodeSpace.UnitTests.Persistence;

/// <summary>
/// Pins the third destination on the main artifact row. <c>workflow_artifact</c> used to hold exactly one of inline
/// bytes or a local <c>storage_url</c>; a routed row holds neither and points at an <c>artifact_object</c> instead,
/// whose <c>artifact_location</c> carries the exact storage-profile revision every read must resolve through.
/// </summary>
[Trait("Category", "Unit")]
public sealed class WorkflowArtifactRoutedStorageSchemaTests
{
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused";

    [Fact]
    public void The_row_carries_a_routed_object_pointer_beside_the_two_legacy_destinations()
    {
        using var db = BuildContext();
        var entity = Entity<WorkflowArtifact>(db);

        entity.GetTableName().ShouldBe("workflow_artifact");
        entity.GetProperties().Select(p => p.Name).Order().ShouldBe(new[]
        {
            "CasArtifactObjectId", "ContentType", "CreatedAt", "Id", "InlineBytes", "Sha256", "SizeBytes",
            "StorageUrl", "TeamId",
        }.Order());
        entity.FindProperty(nameof(WorkflowArtifact.CasArtifactObjectId))!.IsNullable.ShouldBeTrue(
            "every artifact written before routing existed, and every unrouted team's artifact after it, has no CAS object");
    }

    [Fact]
    public void The_routed_pointer_is_tenant_bound_to_the_artifact_object_it_names()
    {
        using var db = BuildContext();
        var entity = Entity<WorkflowArtifact>(db);

        var foreignKey = entity.GetForeignKeys().Single(f => f.PrincipalEntityType.ClrType == typeof(ArtifactObject));

        foreignKey.Properties.Select(p => p.Name).ShouldBe(new[] { "TeamId", "CasArtifactObjectId" },
            "team_id travels with the pointer so one team's artifact row can never name another team's object");
        foreignKey.PrincipalKey.Properties.Select(p => p.Name).ShouldBe(new[] { "TeamId", "Id" });
        foreignKey.DeleteBehavior.ShouldBe(DeleteBehavior.Restrict);
        foreignKey.IsRequired.ShouldBeFalse();
    }

    [Fact]
    public void The_per_team_dedup_key_is_unchanged_by_the_routed_destination()
    {
        using var db = BuildContext();
        var entity = Entity<WorkflowArtifact>(db);

        // PutAsync's idempotency contract is (team, sha) — routing changes WHERE the bytes land, never what makes
        // two payloads the same artifact.
        var dedup = entity.GetIndexes().Single(i => i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(new[] { "TeamId", "Sha256" }));
        dedup.IsUnique.ShouldBeTrue();
    }

    private static CodeSpaceDbContext BuildContext()
    {
        var options = new DbContextOptionsBuilder<CodeSpaceDbContext>()
            .UseNpgsql(UnreachableDatabase)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new CodeSpaceDbContext(options);
    }

    private static IEntityType Entity<TEntity>(CodeSpaceDbContext db) => db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(TEntity)).ShouldNotBeNull();
}
