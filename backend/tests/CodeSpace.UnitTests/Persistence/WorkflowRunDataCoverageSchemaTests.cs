using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.RunData;
using CodeSpace.Messages.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Shouldly;

namespace CodeSpace.UnitTests.Persistence;

[Trait("Category", "Unit")]
public sealed class WorkflowRunDataCoverageSchemaTests
{
    private const string Migration = "0187_workflow_run_data_coverage_snapshot.sql";
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused";

    [Fact]
    public void Applicability_is_a_run_owned_header_and_an_append_only_ordered_member_set()
    {
        using var db = BuildContext();
        var header = Entity<WorkflowRunDataCoverage>(db);
        var member = Entity<WorkflowRunDataCoverageFacet>(db);
        var manifest = Entity<WorkflowRunDataManifest>(db);

        header.GetTableName().ShouldBe(WorkflowRunDataNames.DataCoverage);
        member.GetTableName().ShouldBe(WorkflowRunDataNames.DataCoverageFacet);
        header.GetCheckConstraints().Select(constraint => constraint.Name).ShouldBe(new[]
        {
            "ck_workflow_run_data_coverage_bounds", "ck_workflow_run_data_coverage_state", "ck_workflow_run_data_coverage_time",
        }, ignoreOrder: true);
        member.GetCheckConstraints().Select(constraint => constraint.Name).ShouldBe(new[]
        {
            "ck_workflow_run_data_coverage_facet_bounds", "ck_workflow_run_data_coverage_facet_name",
        }, ignoreOrder: true);

        member.GetKeys().Single(key => key.GetName() == "ux_workflow_run_data_coverage_facet").Properties
            .Select(property => property.Name).ShouldBe(new[] { "TeamId", "WorkflowRunId", "Facet" });
        member.GetIndexes().Single(index => index.GetDatabaseName() == "ux_workflow_run_data_coverage_ordinal")
            .Properties.Select(property => property.Name).ShouldBe(new[] { "TeamId", "WorkflowRunId", "Ordinal" });
        manifest.GetForeignKeys().ShouldNotContain(key => key.PrincipalEntityType.ClrType == typeof(WorkflowRunDataCoverageFacet),
            "takeover-on-write avoids a full-table validation; the admission trigger binds new statements while legacy rows use the frozen fallback until takeover");
    }

    [Fact]
    public void Migration_seals_at_the_status_boundary_and_keeps_late_accounting_separate_from_new_applicability()
    {
        var sql = File.ReadAllText(MigrationPath());

        sql.ShouldContain("AFTER UPDATE OF status ON workflow_run");
        sql.ShouldContain("PERFORM workflow_run_data_completeness_lock(NEW.team_id, NEW.id)",
            customMessage: "status and applicability must rendezvous with producer advances or a terminal snapshot can tear");
        sql.ShouldContain("coverage_state <> 'Open'",
            customMessage: "a terminal answer may not acquire a new question behind an operator's read");
        sql.ShouldContain("expected_delta <= 0",
            customMessage: "present-only recovery does not prove a conditional producer applied to the run");
        sql.ShouldContain("IF EXISTS (SELECT 1 FROM workflow_run_data_coverage_facet",
            customMessage: "an existing member must remain account-able after sealing; late durable evidence improves its answer");
        sql.ShouldContain("BEFORE INSERT OR UPDATE OR DELETE ON workflow_run_data_coverage_facet");
        sql.ShouldContain("cannot append conditional applicability to a sealed run");
        sql.ShouldContain("baseline_position IS NOT NULL AND NEW.ordinal <> baseline_position",
            customMessage: "terminal bootstrap may fill only the baseline capacity frozen in its header; manifest existence is a torn proxy");
        sql.ShouldContain("BEFORE INSERT OR UPDATE OR DELETE ON workflow_run_data_coverage");
        sql.ShouldContain("NEW.revision <> OLD.revision + 1");
        sql.ShouldContain("NEW.baseline_facets IS DISTINCT FROM OLD.baseline_facets");
        sql.ShouldContain("NEW.expected_record_count",
            customMessage: "the compatibility trigger must preserve zero/null rather than manufacture a positive applicability declaration");
        sql.ShouldContain("PERFORM workflow_run_data_completeness_lock(NEW.team_id, NEW.workflow_run_id)",
            customMessage: "raw header/member inserts rendezvous with terminal status just like the application path");
        DbUpRunner.DiscoverScriptNames().ShouldContain(name => name.EndsWith(Migration, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Migration_is_online_by_construction_and_has_no_status_cutover_population_to_reconcile()
    {
        var sql = File.ReadAllText(MigrationPath());

        sql.ShouldNotContain("ALTER TABLE workflow_run_data_manifest",
            customMessage: "DbUp holds every table lock until the whole upgrade commits; touching the existing manifest table here would turn the later coverage work into a writer outage");
        sql.ShouldNotContain("ALTER TABLE workflow_run_capture_gap",
            customMessage: "coverage metadata is not an artifact owner vocabulary change and must not lock the existing gap plane");
        sql.ShouldNotContain("GROUP BY manifest.team_id, manifest.workflow_run_id, run.status",
            customMessage: "bulk-populating headers before the status trigger becomes visible creates a terminal-transition cutover race; historical runs stay on the frozen legacy read shape until first takeover");
        sql.ShouldNotContain("fk_workflow_run_data_manifest_coverage_facet",
            customMessage: "a full-table FK validation is precisely the scan this online migration refuses");
        sql.ShouldContain("workflow_run_data_coverage_ensure",
            customMessage: "new runs and the first post-upgrade writer of a legacy run still need a generic takeover path");
        sql.TrimEnd().ShouldEndWith("EXECUTE FUNCTION workflow_run_data_manifest_admit_coverage();",
            customMessage: "DbUp retains old-table trigger locks until commit, so no scan or data work may follow the final manifest trigger");
    }

    [Fact]
    public void Reader_uses_the_run_snapshot_and_never_the_deployments_current_baseline()
    {
        var reader = File.ReadAllText(Path.Combine(ProductionRoot(), "CodeSpace.Core", "Services", "RunData", "IRunDataCompletenessReader.cs"));

        reader.ShouldContain("WorkflowRunDataCoverageFacet");
        reader.ShouldContain("CoverageState");
        reader.ShouldContain("RunDataManifestCoverage.LegacyV1Facets",
            customMessage: "a historical run with no coverage header uses the frozen v1 question, never today's mutable deployment list");
        reader.ShouldContain("rows[0].BaselineFacets",
            customMessage: "partial member materialization must not shrink the immutable question captured in the header");
        reader.ShouldContain("covered.Concat(legacy)",
            customMessage: "status, coverage membership and legacy statements must share one PostgreSQL statement snapshot");
        (reader.Split(".ToListAsync(", StringSplitOptions.None).Length - 1).ShouldBe(1,
            "a second materialization would let terminalization or first takeover tear the observation between statements");
        reader.ShouldNotContain("RunDataManifestCoverage.RequiredFacets",
            customMessage: "reading today's process constant would retroactively change yesterday's terminal run");
    }

    [Fact]
    public void Modelled_constraints_are_byte_equivalent_to_the_migration_after_whitespace()
    {
        var migration = Normalize(File.ReadAllText(MigrationPath()));
        using var db = BuildContext();
        var constraints = new[] { Entity<WorkflowRunDataCoverage>(db), Entity<WorkflowRunDataCoverageFacet>(db) }
            .SelectMany(entity => entity.GetCheckConstraints()).ToList();

        constraints.ShouldNotBeEmpty();
        foreach (var constraint in constraints) migration.ShouldContain(Normalize(constraint.Sql!),
            customMessage: $"{constraint.Name} drifted between EF and the schema production actually runs");
    }

    private static string MigrationPath() => Path.Combine(AppContext.BaseDirectory, "Persistence", "DbUpFiles", Migration);
    private static string ProductionRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "backend", "src");
            if (Directory.Exists(candidate)) return candidate;
        }

        throw new DirectoryNotFoundException($"backend/src was not found above {AppContext.BaseDirectory}");
    }
    private static string Normalize(string sql) => string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static CodeSpaceDbContext BuildContext()
    {
        var options = new DbContextOptionsBuilder<CodeSpaceDbContext>().UseNpgsql(UnreachableDatabase).UseSnakeCaseNamingConvention().Options;
        return new CodeSpaceDbContext(options);
    }

    private static IEntityType Entity<TEntity>(CodeSpaceDbContext db) => db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(TEntity)).ShouldNotBeNull();
}
