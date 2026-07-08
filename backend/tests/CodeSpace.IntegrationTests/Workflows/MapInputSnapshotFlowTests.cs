using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Variables;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Slice 1 — the durable per-map input snapshot (<c>WorkflowRunMapInput</c>), against real Postgres + the real
/// engine. The contract: a flow.map's resolved collection is frozen ONCE at the first fan-out and read by every
/// later re-entry INSTEAD of re-resolving the <c>items</c> binding — so a changed upstream output between suspend
/// and resume can never shift the branch space out from under the index-keyed branch replay + ordered reduce.
///
/// <para>Pins: (a) first fan-out writes exactly one row whose array equals the live branch space; (b) a 0-element
/// map writes an ElementCount=0 row and stays a no-op; (c) a PRE-EXISTING snapshot row is read VERBATIM instead of
/// re-resolving live items (the crash-replay / get-or-create idempotency property, deterministic); (d) a nested map
/// keys its snapshot by the enclosing-container iteration key (one row per outer branch); (e) a real suspend→resume
/// where the upstream output DRIFTS reads the snapshot, not the drift, and the resolve-to-null short-circuit cannot
/// zero a completed map. Sibling of <see cref="MapDurableResumeFlowTests"/> (the suspend/resume harness).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class MapInputSnapshotFlowTests
{
    private readonly PostgresFixture _fixture;

    public MapInputSnapshotFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task First_fan_out_writes_exactly_one_snapshot_row_whose_frozen_array_equals_the_branch_space()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, SyncMapDefinition("{{trigger.things}}"));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: """{ "things": ["a", "b", "c"] }""");

        await RunEngineAsync(runId);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

        var rows = await db.WorkflowRunMapInput.AsNoTracking().Where(s => s.RunId == runId).ToListAsync();
        rows.Count.ShouldBe(1, "the map fans out once → exactly one snapshot row");
        var snap = rows[0];
        snap.MapNodeId.ShouldBe("map");
        snap.IterationKey.ShouldBe("", "a top-level map keys its snapshot under the empty enclosing-container key");
        snap.ElementCount.ShouldBe(3);
        snap.Sensitivity.ShouldBe("Plain", "non-secret items → the frozen array is stored");
        snap.ContentHash.ShouldNotBeNullOrEmpty();
        snap.DefinitionHash.ShouldNotBeNullOrEmpty();
        snap.ElementsJson.ShouldNotBeNull();
        JsonDocument.Parse(snap.ElementsJson!).RootElement.EnumerateArray().Select(e => e.GetString()).ShouldBe(new[] { "a", "b", "c" });

        // The frozen array IS the live branch space: the reduced results echo each element in order.
        var outputs = JsonDocument.Parse((await MapNodeAsync(db, runId)).OutputsJson).RootElement;
        outputs.GetProperty("count").GetInt32().ShouldBe(3);
        var results = outputs.GetProperty("results");
        for (var i = 0; i < 3; i++) results[i].GetProperty("item").GetString().ShouldBe(new[] { "a", "b", "c" }[i]);
    }

    [Fact]
    public async Task An_empty_collection_writes_a_zero_count_snapshot_row_and_the_map_stays_a_no_op()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, SyncMapDefinition("{{trigger.things}}"));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: """{ "things": [] }""");

        await RunEngineAsync(runId);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

        var snap = await db.WorkflowRunMapInput.AsNoTracking().SingleAsync(s => s.RunId == runId);
        snap.ElementCount.ShouldBe(0, "a genuinely empty collection is recorded as count 0 — distinct from a transient resolve-to-null");
        snap.Sensitivity.ShouldBe("Plain");

        JsonDocument.Parse((await MapNodeAsync(db, runId)).OutputsJson).RootElement.GetProperty("count").GetInt32()
            .ShouldBe(0, "the empty map stays a no-op");
    }

    [Fact]
    public async Task A_preexisting_snapshot_row_is_read_verbatim_instead_of_re_resolving_the_live_items()
    {
        // The crash-replay / get-or-create idempotency property, proven deterministically WITHOUT a suspend: pre-seed
        // the snapshot for (run, map) with an array that DISAGREES with the live trigger items. If the engine reads
        // the snapshot (the contract) the map fans out over the frozen ["Z"]; if it re-resolved live it would fan over
        // the trigger's ["a","b","c"]. The result count + element prove which path ran — no mock, real reduce.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, SyncMapDefinition("{{trigger.things}}"));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: """{ "things": ["a", "b", "c"] }""");

        using (var seed = _fixture.BeginScope())
        {
            var db = seed.Resolve<CodeSpaceDbContext>();
            db.WorkflowRunMapInput.Add(new WorkflowRunMapInput
            {
                Id = Guid.NewGuid(), RunId = runId, MapNodeId = "map", IterationKey = "",
                DefinitionHash = "preseed", ElementCount = 1, ElementsJson = """["Z"]""",
                ContentHash = "preseed", Sensitivity = "Plain", CapturedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await RunEngineAsync(runId);

        using var scope = _fixture.BeginScope();
        var fdb = scope.Resolve<CodeSpaceDbContext>();
        (await fdb.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

        (await fdb.WorkflowRunMapInput.AsNoTracking().CountAsync(s => s.RunId == runId))
            .ShouldBe(1, "the engine found the existing row and did NOT insert a second");

        var outputs = JsonDocument.Parse((await MapNodeAsync(fdb, runId)).OutputsJson).RootElement;
        outputs.GetProperty("count").GetInt32().ShouldBe(1, "the map fanned over the FROZEN ['Z'], not the live ['a','b','c']");
        outputs.GetProperty("results")[0].GetProperty("item").GetString().ShouldBe("Z");
    }

    [Fact]
    public async Task A_nested_map_snapshots_one_row_per_outer_branch_keyed_by_the_enclosing_iteration_key()
    {
        // The map's own enclosing-container key (NOT the per-branch index key) is the snapshot key. A nested map fans
        // once per OUTER branch, so it writes one row per outer iteration key "outer#i" — proving the key is the map's
        // own nodeIterationKey. We only need the first walk (all leaves park) to observe the rows.
        var key = "sp-" + Guid.NewGuid().ToString("N");
        SuspendProbeNode.Reset(key);

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, NestedSuspendingMapDefinition(key));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: """{ "things": ["o0", "o1"] }""");

        await RunEngineAsync(runId);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Suspended);

        var rows = await db.WorkflowRunMapInput.AsNoTracking().Where(s => s.RunId == runId).ToListAsync();

        // The OUTER map: one row, top-level key "".
        rows.Count(s => s.MapNodeId == "outer").ShouldBe(1);
        rows.Single(s => s.MapNodeId == "outer").IterationKey.ShouldBe("");
        rows.Single(s => s.MapNodeId == "outer").ElementCount.ShouldBe(2);

        // The INNER map: one row per OUTER branch, keyed by the enclosing "outer#i" — NOT the per-branch "inner#j".
        // A nested map keys its snapshot by the enclosing-container iteration, one row per outer pass.
        var innerKeys = rows.Where(s => s.MapNodeId == "inner").Select(s => s.IterationKey).OrderBy(k => k).ToList();
        innerKeys.ShouldBe(new[] { "outer#0", "outer#1" });
    }

    [Fact]
    public async Task A_suspend_resume_where_the_upstream_output_drifts_reads_the_frozen_snapshot_not_the_drift()
    {
        // THE headline realistic scenario. A planner emits the items array; the map fans out + suspends per branch;
        // then the planner's PERSISTED output is mutated (reorder+insert+delete) the way a non-deterministic upstream
        // would on a crash-resume re-walk. On resume the map MUST read the frozen snapshot, so branch index i resumes
        // against the SAME element it parked on — proven by the per-index item AND its own resolved payload.
        var key = "sp-" + Guid.NewGuid().ToString("N");
        SuspendProbeNode.Reset(key);

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, PlannerSuspendingMapDefinition(key, """["a", "b", "c"]"""));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: "{}");

        await RunEngineAsync(runId);
        await AssertSuspendedAsync(runId, "three branches parked on the planner-emitted array");

        // Inject the drift: the planner's persisted output array becomes a reorder+insert+delete that would shift indices.
        await MutatePlannerThingsAsync(runId, """["X", "a", "c"]""");

        (await ResolveBranchAsync(runId, key, "a", "RES-a")).ShouldBeTrue();
        await AssertSuspendedAsync(runId, "two branches still pending");
        (await ResolveBranchAsync(runId, key, "b", "RES-b")).ShouldBeTrue();
        await AssertSuspendedAsync(runId, "one branch still pending");
        (await ResolveBranchAsync(runId, key, "c", "RES-c")).ShouldBeTrue();
        await RunEngineAsync(runId);

        using var done = _fixture.BeginScope();
        var fdb = done.Resolve<CodeSpaceDbContext>();
        (await fdb.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

        var outputs = JsonDocument.Parse((await MapNodeAsync(fdb, runId)).OutputsJson).RootElement;
        outputs.GetProperty("count").GetInt32().ShouldBe(3, "the branch space stayed the ORIGINAL 3 elements, not the mutated array");
        var results = outputs.GetProperty("results");
        for (var i = 0; i < 3; i++)
        {
            results[i].GetProperty("item").GetString().ShouldBe(new[] { "a", "b", "c" }[i], "branch i resumed against its ORIGINAL element, not the drifted one");
            results[i].GetProperty("summary").GetString().ShouldBe(new[] { "RES-a", "RES-b", "RES-c" }[i]);
        }

        var snapRows = await fdb.WorkflowRunMapInput.AsNoTracking().Where(s => s.RunId == runId).ToListAsync();
        snapRows.Count.ShouldBe(1, "the re-walk found the snapshot, did not rewrite it");
        JsonDocument.Parse(snapRows[0].ElementsJson!).RootElement.EnumerateArray().Select(e => e.GetString()).ShouldBe(new[] { "a", "b", "c" });
    }

    [Fact]
    public async Task A_resume_where_the_upstream_output_vanishes_to_null_does_not_zero_a_completed_map()
    {
        // The empty-vs-null hazard: ResolveMapElements treats a missing/null items as an empty (no-op) collection. On
        // resume that would zero an already-fanned-out, half-completed map. The snapshot's ElementCount overrides the
        // live resolve-to-null, so the map keeps its 2 branches and reduces them.
        var key = "sp-" + Guid.NewGuid().ToString("N");
        SuspendProbeNode.Reset(key);

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, PlannerSuspendingMapDefinition(key, """["a", "b"]"""));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: "{}");

        await RunEngineAsync(runId);
        await AssertSuspendedAsync(runId, "two branches parked");

        // The upstream output vanishes on the re-walk: the planner's array → JSON null.
        await MutatePlannerThingsToNullAsync(runId);

        (await ResolveBranchAsync(runId, key, "a", "RES-a")).ShouldBeTrue();
        (await ResolveBranchAsync(runId, key, "b", "RES-b")).ShouldBeTrue();
        await RunEngineAsync(runId);

        using var done = _fixture.BeginScope();
        var fdb = done.Resolve<CodeSpaceDbContext>();
        (await fdb.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

        var outputs = JsonDocument.Parse((await MapNodeAsync(fdb, runId)).OutputsJson).RootElement;
        outputs.GetProperty("count").GetInt32().ShouldBe(2, "the snapshot overrides the live resolve-to-null — the map is NOT zeroed");
        outputs.GetProperty("results")[0].GetProperty("summary").GetString().ShouldBe("RES-a");
        outputs.GetProperty("results")[1].GetProperty("summary").GetString().ShouldBe("RES-b");
    }

    [Fact]
    public async Task A_secret_bound_collection_is_classified_secret_derived_and_never_frozen_in_the_snapshot()
    {
        // The map's items bind to a Secret-typed variable. The snapshot must classify SecretDerived and store NO frozen
        // array (re-resolved live), so a plaintext secret never lands at rest in the snapshot row.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using (var setup = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            var secretValue = JsonSerializer.Deserialize<JsonElement>("\"SECRET-SENTINEL\"");
            await setup.Resolve<IVariableService>().SetAsync(
                VariableScope.Team, teamId, teamId, "API_KEY", VariableValueType.Secret,
                secretValue, null, userId, CancellationToken.None);
        }

        // items is a literal array with a secret-referencing element; the body echoes the INDEX (not the value), so the
        // only place the secret could land at rest is the snapshot — which must NULL it.
        var workflowId = await CreateWorkflowAsync(teamId, userId, SecretItemsMapDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: "{}");

        await RunEngineAsync(runId);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.Status.ShouldBe(WorkflowRunStatus.Success, "run failed: " + run.OutputsJson);

        var snap = await db.WorkflowRunMapInput.AsNoTracking().SingleAsync(s => s.RunId == runId);
        snap.Sensitivity.ShouldBe("SecretDerived", "a map whose items reference a secret path is SecretDerived");
        snap.ElementsJson.ShouldBeNull("a SecretDerived collection stores no frozen array (re-resolved live)");
        snap.ElementCount.ShouldBe(2, "the count is recorded (not secret) even when the array is not");

        // Sentinel sweep: the secret plaintext never appears in any snapshot row.
        (await db.WorkflowRunMapInput.AsNoTracking().CountAsync(s => s.RunId == runId && s.ElementsJson != null && s.ElementsJson.Contains("SECRET")))
            .ShouldBe(0, "no secret plaintext is frozen in the map-input snapshot");

        // The map still fanned out over the 2 secret elements (the live in-memory array drove the first walk).
        JsonDocument.Parse((await MapNodeAsync(db, runId)).OutputsJson).RootElement.GetProperty("count").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task A_large_collection_offloads_on_write_and_re_inflates_on_resume_to_drive_the_branch_space()
    {
        // A large frozen array is offloaded to the artifact store on write (a compact ref, not the full inline array);
        // a real suspend→resume then RE-INFLATES it on read and drives the resumed branch space — the full round-trip.
        var key = "sp-" + Guid.NewGuid().ToString("N");
        SuspendProbeNode.Reset(key);

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var elems = Enumerable.Range(0, 16).Select(i => $"big-{i}-{new string('x', 600)}").ToList();
        var workflowId = await CreateWorkflowAsync(teamId, userId, TriggerSuspendingMapDefinition(key));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: JsonSerializer.Serialize(new { things = elems }));

        await RunEngineAsync(runId);
        await AssertSuspendedAsync(runId, "16 branches parked over a large array");

        var snap = await SingleSnapshotAsync(runId);
        snap.ElementCount.ShouldBe(16);
        snap.ElementsJson!.Length.ShouldBeLessThan(elems.Sum(s => s.Length), "the large array is offloaded to a compact artifact ref, not stored inline");

        for (var i = 0; i < elems.Count; i++) (await ResolveBranchAsync(runId, key, $"b-{i}", "ok")).ShouldBeTrue();
        await RunEngineAsync(runId);

        using var done = _fixture.BeginScope();
        var fdb = done.Resolve<CodeSpaceDbContext>();
        (await fdb.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

        // count==16 on resume proves the offloaded snapshot re-inflated to the full array — a failed re-inflate would
        // fan over 0 elements and orphan the 16 parked branches.
        JsonDocument.Parse((await MapNodeAsync(fdb, runId)).OutputsJson).RootElement.GetProperty("count").GetInt32()
            .ShouldBe(16, "the offloaded snapshot re-inflated to all 16 elements on resume");
    }

    [Fact]
    public async Task A_secret_derived_map_resumes_when_its_count_is_stable_then_the_count_guard_holds()
    {
        // The SecretDerived read path re-resolves live (no frozen array), but the frozen ElementCount guards the branch
        // space: a secret-bound map that suspends and resumes with a STABLE size re-resolves the current secret values
        // and proceeds (live count == frozen count). The guard fails closed only if the size drifts.
        var key = "sp-" + Guid.NewGuid().ToString("N");
        SuspendProbeNode.Reset(key);

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using (var setup = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            await setup.Resolve<IVariableService>().SetAsync(VariableScope.Team, teamId, teamId, "API_KEY", VariableValueType.Secret,
                JsonSerializer.Deserialize<JsonElement>("\"SECRET-SENTINEL\""), null, userId, CancellationToken.None);

        var workflowId = await CreateWorkflowAsync(teamId, userId, SecretSuspendingMapDefinition(key));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: "{}");

        await RunEngineAsync(runId);
        await AssertSuspendedAsync(runId, "two secret-derived branches parked");

        var snap = await SingleSnapshotAsync(runId);
        snap.Sensitivity.ShouldBe("SecretDerived");
        snap.ElementsJson.ShouldBeNull();
        snap.ElementCount.ShouldBe(2);

        (await ResolveBranchAsync(runId, key, "branch-0", "RES-0")).ShouldBeTrue();
        (await ResolveBranchAsync(runId, key, "branch-1", "RES-1")).ShouldBeTrue();
        await RunEngineAsync(runId);

        using var done = _fixture.BeginScope();
        var fdb = done.Resolve<CodeSpaceDbContext>();
        (await fdb.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Success, "the count guard passed (live re-resolve count == frozen ElementCount) and the map resumed");
        JsonDocument.Parse((await MapNodeAsync(fdb, runId)).OutputsJson).RootElement.GetProperty("count").GetInt32().ShouldBe(2);
    }

    // ── Definitions ──

    // start → map(items=<binding>; body: ms → leaf[JsonEmit echoes {item}]) → terminal. Synchronous (no suspend),
    // so the run completes in one walk. JsonEmitNode is the body terminal (a map body terminal must produce SCOPE
    // outputs, so nodes.map.outputs.results[i] = { item }).
    private static WorkflowDefinition SyncMapDefinition(string itemsBinding) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "map", TypeKey = "flow.map", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json($$"""{ "items": "{{itemsBinding}}" }""") },
            new() { Id = "ms", TypeKey = "flow.map_start", ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "leaf", TypeKey = JsonEmitNode.Key, ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "item": "{{item}}" }""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "count": "{{nodes.map.outputs.count}}" }""") },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "map" },
            new() { From = "map", To = "end" },
            new() { From = "ms", To = "leaf" },
        },
    };

    // NESTED map-in-map with a suspending inner leaf — mirrors MapDurableResumeFlowTests.NestedSuspendingMapDefinition.
    private static WorkflowDefinition NestedSuspendingMapDefinition(string key) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "outer", TypeKey = "flow.map", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "items": "{{trigger.things}}" }""") },
            new() { Id = "mso", TypeKey = "flow.map_start", ParentId = "outer", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "inner", TypeKey = "flow.map", ParentId = "outer", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "items": [ "{{item}}::j0", "{{item}}::j1" ] }""") },
            new() { Id = "mst", TypeKey = "flow.map_start", ParentId = "inner", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "leaf", TypeKey = SuspendProbeNode.Key, ParentId = "inner", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "key": "__KEY__", "item": "{{item}}" }""".Replace("__KEY__", key)) },
            new() { Id = "outerTerm", TypeKey = JsonEmitNode.Key, ParentId = "outer", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "results": "{{nodes.inner.outputs.results}}" }""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "count": "{{nodes.outer.outputs.count}}" }""") },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "outer" },
            new() { From = "outer", To = "end" },
            new() { From = "mso", To = "inner" },
            new() { From = "inner", To = "outerTerm" },
            new() { From = "mst", To = "leaf" },
        },
    };

    // manual → planner[JsonEmit json.things=<array>] → map(items={{nodes.planner.outputs.json.things}}; body: ms →
    // leaf[SuspendProbe item={{item}}]) → terminal. The planner's persisted output is the mutable upstream the drift /
    // resolve-to-null tests perturb between suspend and resume.
    private static WorkflowDefinition PlannerSuspendingMapDefinition(string key, string thingsJsonArray) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "planner", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json($$"""{ "json": { "things": {{thingsJsonArray}} } }""") },
            new() { Id = "map", TypeKey = "flow.map", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "items": "{{nodes.planner.outputs.json.things}}" }""") },
            new() { Id = "ms", TypeKey = "flow.map_start", ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "leaf", TypeKey = SuspendProbeNode.Key, ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "key": "__KEY__", "item": "{{item}}" }""".Replace("__KEY__", key)) },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "count": "{{nodes.map.outputs.count}}" }""") },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "planner" },
            new() { From = "planner", To = "map" },
            new() { From = "map", To = "end" },
            new() { From = "ms", To = "leaf" },
        },
    };

    // map(items={{team.SECRET_THINGS}}; body: ms → leaf[JsonEmit {idx:{{index}}}]) → terminal. The body echoes the
    // INDEX, never {{item}}, so a secret element value never lands in a node output — the only at-rest surface is the
    // snapshot, which must NULL it.
    private static WorkflowDefinition SecretItemsMapDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "map", TypeKey = "flow.map", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "items": [ "{{team.API_KEY}}", "plain-elem" ] }""") },
            new() { Id = "ms", TypeKey = "flow.map_start", ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "leaf", TypeKey = JsonEmitNode.Key, ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "idx": "{{index}}" }""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "count": "{{nodes.map.outputs.count}}" }""") },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "map" },
            new() { From = "map", To = "end" },
            new() { From = "ms", To = "leaf" },
        },
    };

    // A trigger-bound map whose body SUSPENDS (items={{trigger.things}}). Used for the offload round-trip: the array
    // comes from the trigger (never an intermediate node output, which would itself offload), so only the SNAPSHOT offloads.
    private static WorkflowDefinition TriggerSuspendingMapDefinition(string key) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "map", TypeKey = "flow.map", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "items": "{{trigger.things}}" }""") },
            new() { Id = "ms", TypeKey = "flow.map_start", ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "leaf", TypeKey = SuspendProbeNode.Key, ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "key": "__KEY__", "item": "b-{{index}}" }""".Replace("__KEY__", key)) },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "count": "{{nodes.map.outputs.count}}" }""") },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "map" },
            new() { From = "map", To = "end" },
            new() { From = "ms", To = "leaf" },
        },
    };

    // A secret-derived map (items references a secret path) whose body SUSPENDS — for the count-guard test. The leaf
    // parks under "branch-<index>" (never the element value), so the secret never lands in the wait token.
    private static WorkflowDefinition SecretSuspendingMapDefinition(string key) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "map", TypeKey = "flow.map", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "items": [ "{{team.API_KEY}}", "plain" ] }""") },
            new() { Id = "ms", TypeKey = "flow.map_start", ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "leaf", TypeKey = SuspendProbeNode.Key, ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "key": "__KEY__", "item": "branch-{{index}}" }""".Replace("__KEY__", key)) },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "count": "{{nodes.map.outputs.count}}" }""") },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "map" },
            new() { From = "map", To = "end" },
            new() { From = "ms", To = "leaf" },
        },
    };

    // ── Helpers ──

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private static async Task<Core.Persistence.Entities.WorkflowRunNode> MapNodeAsync(CodeSpaceDbContext db, Guid runId, string mapNodeId = "map") =>
        await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == mapNodeId && n.IterationKey == "");

    private async Task<WorkflowRunMapInput> SingleSnapshotAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunMapInput.AsNoTracking().SingleAsync(s => s.RunId == runId && s.MapNodeId == "map");
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "map-snapshot-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = definition,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task AssertSuspendedAsync(Guid runId, string because)
    {
        using var scope = _fixture.BeginScope();
        (await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Suspended, because);
    }

    // Resolve ONE branch's wait via the wait-for-all barrier path (the agent/sub-workflow completion route), located
    // by the SuspendProbe correlation token "<key>::<element>".
    private async Task<bool> ResolveBranchAsync(Guid runId, string key, string element, string summary)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var waitId = await db.WorkflowRunWait.AsNoTracking()
            .Where(w => w.RunId == runId && w.Token == $"{key}::{element}")
            .Select(w => w.Id).SingleAsync();

        var payload = JsonSerializer.Serialize(new { summary });
        return await scope.Resolve<IWorkflowResumeService>().ResumeOnWaitCompletionAsync(runId, waitId, payload, CancellationToken.None);
    }

    // Drift the planner's PERSISTED output (the upstream the map's items bind to) — the fault a non-deterministic
    // upstream would produce on a crash-resume re-walk. The ledger is APPEND-ONLY, so we append a fresh node.completed
    // (the workflow_run_node view projects the LATEST record), which the re-walk rebuilds scope from.
    private async Task MutatePlannerThingsAsync(Guid runId, string newThingsJson)
    {
        using var scope = _fixture.BeginScope();
        var outputs = new Dictionary<string, JsonElement>
        {
            ["json"] = JsonSerializer.Deserialize<JsonElement>($$"""{ "things": {{newThingsJson}} }"""),
        };
        await scope.Resolve<IRunRecordLogger>().NodeCompletedAsync(runId, "planner", "", outputs, null, TimeSpan.Zero, CancellationToken.None);
    }

    private Task MutatePlannerThingsToNullAsync(Guid runId) => MutatePlannerThingsAsync(runId, "null");
}
