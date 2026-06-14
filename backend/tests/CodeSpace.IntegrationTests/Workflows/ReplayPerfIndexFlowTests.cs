using System.Text;
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
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// PR-D5 — proves the durable-resume replay path scales at large/nested map + loop fan-out, against real Postgres
/// + the real schema (migration 0053). The map + loop rehydrate paths re-enter a suspended run by reading every
/// branch / iteration row under a RUN-SCOPED ITERATION-KEY PREFIX:
///
///   RehydrateMapResultsAsync: <c>WHERE run_id = @r AND iteration_key LIKE '&lt;mapId&gt;#%'</c>
///   RehydrateLoopStateAsync:  <c>WHERE run_id = @r AND iteration_key LIKE '&lt;loopId&gt;#%'</c>
///
/// both via the <c>workflow_run_node</c> VIEW (EF's <c>IterationKey.StartsWith(prefix)</c> translates to that LIKE).
/// Before 0053 the only run-scoped index led with <c>node_id</c> (0015 <c>idx_wrr_run_node</c>) or was <c>run_id</c>
/// only (<c>idx_wrr_run_sequence</c>), so the prefix predicate could only be applied as a post-scan FILTER — a scan
/// of EVERY node row of the run (1k / 10k branches) on every rehydrate. 0053 adds
/// <c>idx_wrr_run_iteration_prefix (run_id, iteration_key text_pattern_ops) WHERE node_id IS NOT NULL</c>, so the
/// planner pushes the prefix into the index as a range.
///
/// <para>Fidelity: 🟢 High — real Postgres, the real schema/view from DbUp, and the EXPLAIN runs against the EXACT
/// SQL EF produces for the production rehydrate query (captured via <c>ToQueryString()</c>, parameters re-bound by
/// name), so this asserts on the literal plan the engine's query gets — not a hand-rewritten approximation. The
/// assertion parses the plan for the new index name + an Index/Bitmap scan and rejects a Seq Scan on
/// <c>workflow_run_record</c>; EXPLAIN-plan assertion is preferred over flaky wall-clock timing (none asserted).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ReplayPerfIndexFlowTests
{
    private const string IndexName = "idx_wrr_run_iteration_prefix";
    private const int BranchCount = 1000;

    private readonly PostgresFixture _fixture;

    public ReplayPerfIndexFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Migration_0053_creates_the_iteration_key_prefix_index()
    {
        // DbUpRunner assertion (also pinned in DbUpRunnerTests.Index_exists_after_migration); kept here so the
        // perf suite's own precondition is self-checking — if the index is absent, the EXPLAIN tests below are
        // meaningless, so fail fast with the diagnostic.
        (await IndexExistsAsync(IndexName).ConfigureAwait(false)).ShouldBeTrue(
            $"Index '{IndexName}' must exist after migration 0053_run_record_iteration_key_prefix_index.sql — " +
            $"the durable-resume rehydrate prefix scan depends on it. Diagnose: psql -c '\\di {IndexName}'.");
    }

    [Fact]
    public async Task Map_rehydrate_prefix_scan_uses_the_index_at_1000_branches_not_a_seq_scan()
    {
        var runId = await SeedRunWithBranchRowsAsync("map", BranchCount, withNested: true).ConfigureAwait(false);

        var plan = await ExplainRehydrateQueryAsync(runId, prefix: "map#").ConfigureAwait(false);

        AssertIndexScanNotSeqScan(plan, "map");
    }

    [Fact]
    public async Task Loop_rehydrate_prefix_scan_uses_the_index_at_1000_iterations_not_a_seq_scan()
    {
        // The loop rehydrate (RehydrateLoopStateAsync) has the IDENTICAL query shape — run_id + iteration_key
        // prefix — only the prefix token differs ('<loopId>#' vs '<mapId>#'). The same index serves both.
        var runId = await SeedRunWithBranchRowsAsync("loop", BranchCount, withNested: false).ConfigureAwait(false);

        var plan = await ExplainRehydrateQueryAsync(runId, prefix: "loop#").ConfigureAwait(false);

        AssertIndexScanNotSeqScan(plan, "loop");
    }

    /// <summary>
    /// Seed a real run, then append <paramref name="count"/> node.completed ledger rows under keys
    /// "&lt;container&gt;#0".."#&lt;count-1&gt;" (one terminal-body node row per branch — what the engine writes when a
    /// branch settles). Optionally adds nested-descendant rows ("&lt;container&gt;#i/inner#j") to mirror a map-in-map,
    /// plus a large block of OTHER-run rows so a Seq Scan would be genuinely expensive (the planner only prefers the
    /// index when the alternative actually costs something). Direct INSERT is faithful: the ledger is append-only
    /// (the immutability trigger blocks only UPDATE/DELETE), and node.completed is exactly the engine's settle row.
    /// </summary>
    private async Task<Guid> SeedRunWithBranchRowsAsync(string container, int count, bool withNested)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture).ConfigureAwait(false);
        var workflowId = await CreateBareWorkflowAsync(teamId, userId).ConfigureAwait(false);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId).ConfigureAwait(false);

        // Bulk-insert via a single batched INSERT for speed (1k+ rows). EF's change tracker would be slow here and
        // adds nothing — the rows are plain ledger appends with no relationships to fix up.
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        var sb = new StringBuilder(
            "INSERT INTO workflow_run_record (run_id, record_type, node_id, iteration_key, payload_json) VALUES ");
        var values = new List<string>(count * 2);

        for (var i = 0; i < count; i++)
        {
            values.Add($"('{runId}', '{WorkflowRunRecordTypes.NodeCompleted}', 'terminal', '{container}#{i}', '{{\"outputs\":{{}}}}'::jsonb)");
            if (withNested)
                values.Add($"('{runId}', '{WorkflowRunRecordTypes.NodeCompleted}', 'inner_terminal', '{container}#{i}/inner#0', '{{\"outputs\":{{}}}}'::jsonb)");
        }

        sb.Append(string.Join(",", values));
        await using (var cmd = new NpgsqlCommand(sb.ToString(), conn))
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

        // Noise: many OTHER-run rows so the planner sees a large table where a Seq Scan is the costly alternative.
        // run_id has an FK to workflow_run, so the noise can't use random uuids — seed a handful of REAL extra runs
        // (same workflow/team) and spread the noise rows across them. This mirrors a busy deployment: one run's
        // rehydrate must stay index-served even when the ledger holds many other runs' rows.
        var noiseRunIds = await SeedNoiseRunsAsync(workflowId, teamId, count: 20).ConfigureAwait(false);
        var noiseRunArray = "ARRAY[" + string.Join(",", noiseRunIds.Select(id => $"'{id}'::uuid")) + "]";

        await using (var noise = new NpgsqlCommand(
            "INSERT INTO workflow_run_record (run_id, record_type, node_id, iteration_key, payload_json) " +
            $"SELECT ({noiseRunArray})[1 + (g % {noiseRunIds.Count})], 'node.completed', 'terminal', 'map#' || (g % 1000), '{{\"outputs\":{{}}}}'::jsonb " +
            "FROM generate_series(1, 40000) g", conn))
            await noise.ExecuteNonQueryAsync().ConfigureAwait(false);

        // ANALYZE so the planner has real statistics — without it cost estimates are defaults and the plan choice
        // is not representative of production (where autovacuum keeps stats fresh).
        await using (var analyze = new NpgsqlCommand("ANALYZE workflow_run_record", conn))
            await analyze.ExecuteNonQueryAsync().ConfigureAwait(false);

        return runId;
    }

    /// <summary>Seed <paramref name="count"/> additional real runs (same workflow/team) whose ids back the noise ledger rows — the FK to workflow_run requires real run ids, not random uuids.</summary>
    private async Task<IReadOnlyList<Guid>> SeedNoiseRunsAsync(Guid workflowId, Guid teamId, int count)
    {
        var ids = new List<Guid>(count);
        for (var i = 0; i < count; i++)
            ids.Add(await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId).ConfigureAwait(false));
        return ids;
    }

    /// <summary>
    /// Capture the EXACT SQL EF generates for the production rehydrate query
    /// (<c>_db.WorkflowRunNode.Where(n =&gt; n.RunId == runId &amp;&amp; n.IterationKey.StartsWith(prefix))</c>) via
    /// <c>ToQueryString()</c>, then run <c>EXPLAIN</c> on it. We re-bind the two parameters by name so the EXPLAIN
    /// is against the literal generated SQL — not an approximation. This is faithful by construction: any future
    /// change to how EF translates StartsWith (e.g. a different LIKE escaping) is reflected here automatically.
    /// </summary>
    private async Task<string> ExplainRehydrateQueryAsync(Guid runId, string prefix)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var query = db.WorkflowRunNode.AsNoTracking()
            .Where(n => n.RunId == runId && n.IterationKey.StartsWith(prefix));

        var body = StripParameterHeader(query.ToQueryString());

        // Inline the two known values as SQL literals so the EXPLAIN runs against the literal statement EF emits —
        // no parameter type-inference ambiguity (EF's run_id placeholder is uuid; the StartsWith placeholder is text).
        // The values are test-controlled (a Guid + a metachar-free prefix), so inlining is safe and unambiguous.
        var inlined = InlineParameterValues(body, runId, $"{prefix}%");

        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        await using var cmd = new NpgsqlCommand($"EXPLAIN (COSTS OFF) {inlined}", conn);

        var lines = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
            lines.Add(reader.GetString(0));

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Assert the captured plan is index-served, not a full scan: it names the new index AND uses an Index Scan or
    /// Bitmap Index Scan, and does NOT contain a Seq Scan on workflow_run_record. The message names the watched
    /// signal + how to diagnose manually (Rule 12.10).
    /// </summary>
    private static void AssertIndexScanNotSeqScan(string plan, string container)
    {
        plan.ShouldContain(IndexName,
            customMessage: $"The {container} rehydrate prefix scan must be served by '{IndexName}' (migration 0053), " +
                           $"but the plan does not name it. Run EXPLAIN on the rehydrate query manually to inspect. Plan:\n{plan}");

        (plan.Contains("Index Scan") || plan.Contains("Bitmap Index Scan")).ShouldBeTrue(
            $"The {container} rehydrate plan must use an Index Scan / Bitmap Index Scan on '{IndexName}', not a full scan. Plan:\n{plan}");

        plan.ShouldNotContain("Seq Scan on workflow_run_record",
            customMessage: $"The {container} rehydrate must NOT fall back to a Seq Scan on workflow_run_record at " +
                           $"{BranchCount} branches — that is the O(rows-in-run) regression 0053 fixes. Plan:\n{plan}");
    }

    /// <summary>
    /// EF's <c>ToQueryString()</c> prepends a "-- @paramName='value' (...)" declaration comment block before the
    /// SQL. Strip those comment lines so the EXPLAIN command is just the statement.
    /// </summary>
    private static string StripParameterHeader(string sql) =>
        string.Join("\n", sql.Split('\n').Where(l => !l.TrimStart().StartsWith("--")));

    /// <summary>
    /// Substitute EF's <c>@__name</c> placeholders with SQL literals. The two distinct names are the run-id (the one
    /// adjacent to "run_id =", cast to uuid) and the StartsWith prefix (every OTHER placeholder — Npgsql may emit it
    /// in more than one position for the LIKE / range form). The prefix is a metachar-free LIKE pattern, so a plain
    /// quoted literal is exact.
    /// </summary>
    private static string InlineParameterValues(string sql, Guid runId, string likePattern)
    {
        var runIdParam = FindRunIdParameter(sql);
        var result = sql;

        // Replace longest names first so a shorter name that is a prefix of a longer one (e.g. @__p_0 vs @__p_01)
        // can't corrupt the substitution.
        foreach (var name in ExtractParameterNames(sql).OrderByDescending(n => n.Length))
        {
            var literal = name == runIdParam ? $"'{runId}'::uuid" : $"'{likePattern}'";
            result = result.Replace("@" + name, literal);
        }

        return result;
    }

    /// <summary>The placeholder name immediately to the right of "run_id =" (or "run_id" + "="), i.e. the uuid param.</summary>
    private static string FindRunIdParameter(string sql)
    {
        var idx = sql.IndexOf("run_id", StringComparison.OrdinalIgnoreCase);
        while (idx >= 0)
        {
            var at = sql.IndexOf('@', idx);
            var eq = sql.IndexOf('=', idx);
            // The '@' must be the next token after this run_id, before any other run_id mention.
            if (at >= 0 && eq >= 0 && eq < at && at - idx < 40)
            {
                var j = at + 1;
                while (j < sql.Length && (char.IsLetterOrDigit(sql[j]) || sql[j] == '_')) j++;
                return sql.Substring(at + 1, j - at - 1);
            }
            idx = sql.IndexOf("run_id", idx + 6, StringComparison.OrdinalIgnoreCase);
        }
        throw new InvalidOperationException($"Could not locate the run_id parameter in the EF-generated SQL:\n{sql}");
    }

    private static IEnumerable<string> ExtractParameterNames(string sql)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < sql.Length; i++)
        {
            if (sql[i] != '@') continue;
            var j = i + 1;
            while (j < sql.Length && (char.IsLetterOrDigit(sql[j]) || sql[j] == '_')) j++;
            if (j > i + 1) names.Add(sql.Substring(i + 1, j - i - 1));
            i = j - 1;
        }
        return names;
    }

    private async Task<Guid> CreateBareWorkflowAsync(Guid teamId, Guid userId)
    {
        // A minimal valid workflow just to satisfy workflow_run's FK — the run is never executed here; we only
        // need a real run id to scope the seeded ledger rows. Use the production CreateWorkflowCommand (mirrors
        // every other workflow flow test) so the entity shape never drifts from a hand-built row.
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "perf-wf-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = WorkflowsTestSeed.MinimalDefinition(),
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        }).ConfigureAwait(false);
    }

    private async Task<bool> IndexExistsAsync(string indexName)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        await using var cmd = new NpgsqlCommand(
            "SELECT EXISTS (SELECT FROM pg_indexes WHERE schemaname = 'public' AND indexname = @i)", conn);
        cmd.Parameters.AddWithValue("i", indexName);

        return (bool)(await cmd.ExecuteScalarAsync().ConfigureAwait(false))!;
    }
}
