using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration: migration 0099's HISTORICAL-ROW slug backfill — the branch the fixture (empty DB at init)
/// never exercises. Proves the deploy-blocker fix: a pure ROW_NUMBER-per-partition backfill emits a duplicate
/// slug across base-slug partitions (three alive workflows named "Foo","Foo","Foo 2" → "foo","foo-2" AND
/// "foo-2") → CREATE UNIQUE INDEX raises 23505 → the whole DbUp transaction rolls back → deploy hard-fails.
/// The loop-until-free backfill must give three DISTINCT slugs.
///
/// <para>The pre-migration state (slug NULL, colliding names, no NOT NULL, no unique index) is reconstructed
/// inside a transaction that is ALWAYS rolled back — Postgres DDL is transactional, so the schema surgery
/// never touches the shared collection database.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class WorkflowSlugBackfillTests
{
    private readonly PostgresFixture _fixture;
    public WorkflowSlugBackfillTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task The_0099_backfill_gives_distinct_slugs_to_cross_partition_name_collisions()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        Guid w1, w2, w3;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            var m = scope.Resolve<IMediator>();
            w1 = await m.Send(Create("one"));
            w2 = await m.Send(Create("two"));
            w3 = await m.Send(Create("three"));
        }

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync();

        // Reconstruct the pre-0099 state for these three rows: drop the guards, rename to the colliding names,
        // NULL the slug. All inside the rolled-back transaction, so nothing persists.
        await db.Database.ExecuteSqlRawAsync("DROP INDEX uq_workflow_team_slug_active");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE workflow ALTER COLUMN slug DROP NOT NULL");
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE workflow SET slug = NULL, name = CASE id WHEN {0} THEN 'Foo' WHEN {1} THEN 'Foo' ELSE 'Foo 2' END WHERE id IN ({0}, {1}, {2})",
            w1, w2, w3);

        // The 0099 backfill body, VERBATIM (0099_workflow_slug.sql is immutable once journaled).
        await db.Database.ExecuteSqlRawAsync(Backfill0099);

        // The real proof: rebuilding the unique index is exactly what aborts on a duplicate slug. If it succeeds,
        // the backfill produced no collision. (A regression to the ROW_NUMBER backfill makes THIS line throw 23505.)
        await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX uq_workflow_team_slug_active ON workflow(team_id, slug) WHERE deleted_date IS NULL");

        var slugs = await db.Workflow.AsNoTracking()
            .Where(w => new[] { w1, w2, w3 }.Contains(w.Id))
            .Select(w => w.Slug)
            .ToListAsync();

        slugs.Distinct().Count().ShouldBe(3,
            customMessage: "three colliding-name workflows must get three DISTINCT slugs — the cross-partition '-N' dupe is the deploy-blocker");
        slugs.ShouldContain("foo", "the earliest of the collision keeps the base slug");

        await tx.RollbackAsync();
    }

    private static CreateWorkflowCommand Create(string name) => new()
    {
        Name = name,
        Definition = WorkflowsTestSeed.MinimalDefinition(),
        Activations = new List<WorkflowActivationInput>(),
    };

    /// <summary>The migration 0099 slug-backfill DO block, VERBATIM. Keep byte-identical to 0099_workflow_slug.sql.</summary>
    private const string Backfill0099 = @"
DO $$
DECLARE
    r          RECORD;
    candidate  TEXT;
    n          INT;
BEGIN
    FOR r IN
        SELECT id, team_id,
               CASE WHEN base = '' THEN 'workflow' ELSE base END AS base_slug
        FROM (
            SELECT id, team_id, created_date,
                   RTRIM(LEFT(TRIM(BOTH '-' FROM regexp_replace(lower(name), '[^a-z0-9_]+', '-', 'g')), 64), '-') AS base
            FROM workflow
            WHERE slug IS NULL
        ) s
        ORDER BY team_id, created_date, id
    LOOP
        candidate := r.base_slug;
        n := 1;
        WHILE candidate IN ('runs', 'node-manifests', 'system-variables', 'decisions')
           OR EXISTS (SELECT 1 FROM workflow w
                      WHERE w.team_id = r.team_id AND w.slug = candidate AND w.deleted_date IS NULL AND w.id <> r.id)
        LOOP
            n := n + 1;
            candidate := LEFT(r.base_slug, 64 - LENGTH('-' || n::text)) || '-' || n::text;
        END LOOP;
        UPDATE workflow SET slug = candidate WHERE id = r.id;
    END LOOP;
END $$;";
}
