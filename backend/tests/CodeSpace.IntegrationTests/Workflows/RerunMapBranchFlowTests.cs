using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Map-branch rerun (D7) — re-run ONE branch of a top-level flow.map, reusing the N-1 sibling branches, driving
/// the REAL <see cref="IWorkflowService.RerunMapBranchAsync"/> seam against a real engine + Postgres. The
/// mechanism reuses the existing map replay machinery (<c>RehydrateMapResults</c>/<c>FanOutBranches</c>): the
/// fork pre-seeds the N-1 sibling branch cells (replayed, no re-execution) + omits the map's top-level cell (so
/// the map re-enters) + omits the target branch (so it re-runs); the downstream synthesizer re-runs over the
/// new aggregate.
///
/// <para>Tier: high-fidelity. The crown jewel uses a per-element <see cref="FlakyTestNode"/> (pure, failTimes=0)
/// whose <c>AttemptsFor</c> counter per element proves a reused sibling never re-executes (counter unchanged)
/// while the target branch fires once more. v1 supports PURE branch bodies only; side-effecting / suspendable /
/// nested-container bodies are refused (each pinned).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class RerunMapBranchFlowTests
{
    private readonly PostgresFixture _fixture;

    public RerunMapBranchFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    private const string FourElements = """{ "things": ["e0", "e1", "e2", "e3"] }""";

    // ─────────────────────────────────────────────────────────────────────────────
    //  Crown jewel + happy paths
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rerun_one_map_branch_reuses_siblings_reruns_only_the_target_and_re_synthesizes()
    {
        // CROWN JEWEL. map over 4 elements; each branch's terminal is a per-element counter (FlakyTestNode,
        // failTimes=0). Rerun branch 2: siblings 0/1/3 are REPLAYED (their counter never bumps, zero node.started
        // on the fork), branch 2 re-runs exactly once (counter 1→2), the map re-aggregates in element order, and
        // the downstream synthesizer re-runs over the new results.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var key = "mapbranch-crown-" + Guid.NewGuid().ToString("N");

        var workflowId = await CreateWorkflowAsync(teamId, userId, CountingMapDef(key));
        var originalRunId = await RunFreshAsync(workflowId, teamId, FourElements);
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Success);
        for (var i = 0; i < 4; i++) FlakyTestNode.AttemptsFor($"{key}-e{i}").ShouldBe(1, $"branch e{i} fired once on the original run");

        var rerunId = await RerunMapBranchAsync(originalRunId, "map", 2, teamId, userId);
        await RunEngineAsync(rerunId);

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);
        FlakyTestNode.AttemptsFor($"{key}-e0").ShouldBe(1, "sibling 0 was REPLAYED — its counter must not bump");
        FlakyTestNode.AttemptsFor($"{key}-e1").ShouldBe(1, "sibling 1 was replayed");
        FlakyTestNode.AttemptsFor($"{key}-e2").ShouldBe(2, "the target branch re-ran exactly once");
        FlakyTestNode.AttemptsFor($"{key}-e3").ShouldBe(1, "sibling 3 was replayed");

        // node.started discriminator at the BRANCH grain: reused siblings carry zero starts on the fork; the
        // target branch carries a real start (asserted on the branch terminal `echo`).
        (await BranchStartedCountAsync(rerunId, "echo", "map#0")).ShouldBe(0, "sibling 0 branch did not re-run");
        (await BranchStartedCountAsync(rerunId, "echo", "map#1")).ShouldBe(0);
        (await BranchStartedCountAsync(rerunId, "echo", "map#3")).ShouldBe(0);
        (await BranchStartedCountAsync(rerunId, "echo", "map#2")).ShouldBe(1, "the target branch re-ran");

        // The map re-aggregated in ELEMENT ORDER: results[i].item == "e<i>" for EVERY index (proving siblings
        // slot at their original positions, not shuffled), and attempts == 1 for siblings, 2 for the re-run target.
        var results = await LoadMapResultsAsync(rerunId, "map");
        results.GetArrayLength().ShouldBe(4);
        for (var i = 0; i < 4; i++)
            results[i].GetProperty("item").GetString().ShouldBe($"e{i}", $"results[{i}] must hold element e{i} in order");
        results[0].GetProperty("attempts").GetInt32().ShouldBe(1);
        results[1].GetProperty("attempts").GetInt32().ShouldBe(1);
        results[2].GetProperty("attempts").GetInt32().ShouldBe(2, "the re-run branch's fresh result slots back at index 2");
        results[3].GetProperty("attempts").GetInt32().ShouldBe(1);

        // The downstream synthesizer re-ran over the new aggregate.
        (await NodeStartedCountAsync(rerunId, "synth")).ShouldBe(1, "the synthesizer re-ran over the re-aggregated map results");
        var agg = JsonDocument.Parse((await LoadCellAsync(rerunId, "synth")).OutputsJson).RootElement.GetProperty("agg");
        agg[2].GetProperty("item").GetString().ShouldBe("e2");
        agg[2].GetProperty("attempts").GetInt32().ShouldBe(2, "the synthesizer observed the re-run branch's new value");
    }

    [Fact]
    public async Task Rerun_map_branch_re_walk_before_completion_does_not_re_execute_siblings()
    {
        // Durability: a spurious re-dispatch of the fork (reconciler / duplicate worker) must not re-execute the
        // replayed siblings — exactly-once-per-branch holds across the seeded-replay path (the #301 class).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var key = "mapbranch-rewalk-" + Guid.NewGuid().ToString("N");

        var workflowId = await CreateWorkflowAsync(teamId, userId, CountingMapDef(key));
        var originalRunId = await RunFreshAsync(workflowId, teamId, FourElements);

        var rerunId = await RerunMapBranchAsync(originalRunId, "map", 1, teamId, userId);
        await RunEngineAsync(rerunId);
        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);

        // Re-dispatch + re-walk the (now terminal) fork — a settled run is a no-op, and nothing re-fires.
        await ForceEnqueuedAsync(rerunId);
        await RunEngineAsync(rerunId);

        FlakyTestNode.AttemptsFor($"{key}-e0").ShouldBe(1);
        FlakyTestNode.AttemptsFor($"{key}-e1").ShouldBe(2, "branch 1 re-ran exactly once total — the re-walk did not bump it again");
        FlakyTestNode.AttemptsFor($"{key}-e2").ShouldBe(1);
        FlakyTestNode.AttemptsFor($"{key}-e3").ShouldBe(1);
    }

    [Fact]
    public async Task Rerun_map_branch_in_continue_mode_replays_a_failed_sibling_without_re_firing_it()
    {
        // Continue-mode map where EVERY branch abandons (its body node fails, no error edge) → the map still
        // completes Success with failed=N. Rerun branch 0: siblings 1/2/3's abandon-failure rows are REPLAYED
        // (their failing node never re-fires — the #302 guard, and the proof the seeder faithfully re-emits the
        // abandon body row), only branch 0 re-runs, and the failed count is reconstructed.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var key = "mapbranch-continue-" + Guid.NewGuid().ToString("N");

        var workflowId = await CreateWorkflowAsync(teamId, userId, ContinueModeFailingMapDef(key));
        var originalRunId = await RunFreshAsync(workflowId, teamId, FourElements);
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Success, "continue-mode map completes even with all branches abandoned");
        for (var i = 0; i < 4; i++) FlakyTestNode.AttemptsFor($"{key}-e{i}").ShouldBe(1);

        var rerunId = await RerunMapBranchAsync(originalRunId, "map", 0, teamId, userId);
        await RunEngineAsync(rerunId);

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);
        FlakyTestNode.AttemptsFor($"{key}-e0").ShouldBe(2, "the target branch re-ran (and abandoned again)");
        FlakyTestNode.AttemptsFor($"{key}-e1").ShouldBe(1, "a replayed abandoned sibling MUST NOT re-fire its failing node");
        FlakyTestNode.AttemptsFor($"{key}-e2").ShouldBe(1);
        FlakyTestNode.AttemptsFor($"{key}-e3").ShouldBe(1);

        var mapCell = await LoadCellAsync(rerunId, "map");
        JsonDocument.Parse(mapCell.OutputsJson).RootElement.GetProperty("failed").GetInt32()
            .ShouldBe(4, "the reconstructed aggregate preserves the continue-mode failed count");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Fail-closed gates — each asserts NOTHING was written
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rerun_map_branch_index_out_of_range_is_refused_and_writes_nothing()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var key = "mapbranch-range-" + Guid.NewGuid().ToString("N");
        var workflowId = await CreateWorkflowAsync(teamId, userId, CountingMapDef(key));
        var originalRunId = await RunFreshAsync(workflowId, teamId, FourElements);

        var before = await RunCountAsync(teamId);
        await Should.ThrowAsync<RerunTargetNotFoundException>(async () =>
            await RerunMapBranchAsync(originalRunId, "map", 4, teamId, userId));   // valid indices 0..3
        await Should.ThrowAsync<RerunTargetNotFoundException>(async () =>
            await RerunMapBranchAsync(originalRunId, "map", -1, teamId, userId));

        (await RunCountAsync(teamId)).ShouldBe(before, "an out-of-range branch index must write nothing");
    }

    [Fact]
    public async Task Rerun_map_branch_on_a_non_map_node_is_refused()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var key = "mapbranch-notmap-" + Guid.NewGuid().ToString("N");
        var workflowId = await CreateWorkflowAsync(teamId, userId, CountingMapDef(key));
        var originalRunId = await RunFreshAsync(workflowId, teamId, FourElements);

        await Should.ThrowAsync<RerunTargetNotFoundException>(async () =>
            await RerunMapBranchAsync(originalRunId, "synth", 0, teamId, userId));   // synth is a regular node, not a map
    }

    [Fact]
    public async Task Rerun_map_branch_when_the_original_map_did_not_succeed_is_refused()
    {
        // Terminate-mode map with an always-failing branch → the map FAILS. With no clean aggregate, a branch
        // rerun is refused (this single "map must be Success" gate also subsumes a still-suspended sibling).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var key = "mapbranch-failed-" + Guid.NewGuid().ToString("N");
        var workflowId = await CreateWorkflowAsync(teamId, userId, TerminateModeFailingMapDef(key));
        var originalRunId = await RunFreshAsync(workflowId, teamId, """{ "things": ["only"] }""");
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Failure);

        var before = await RunCountAsync(teamId);
        await Should.ThrowAsync<RerunUpstreamNotReusableException>(async () =>
            await RerunMapBranchAsync(originalRunId, "map", 0, teamId, userId));

        (await RunCountAsync(teamId)).ShouldBe(before, "a rerun of a non-succeeded map must write nothing");
    }

    [Fact]
    public async Task Rerun_map_branch_with_a_side_effecting_body_node_is_refused()
    {
        // v1 supports PURE branch bodies only — a side-effecting body node is refused (the D7-3-gate-inside-a-
        // branch composition is a follow-up). Pin it so lifting the restriction is a visible decision.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var probeKey = "mapbranch-sebody-" + Guid.NewGuid().ToString("N");
        MutatingProbeNode.Reset(probeKey);
        var workflowId = await CreateWorkflowAsync(teamId, userId, SideEffectingBodyMapDef(probeKey));
        var originalRunId = await RunFreshAsync(workflowId, teamId, FourElements);

        var before = await RunCountAsync(teamId);
        var ex = await Should.ThrowAsync<RerunBlockedByUnsupportedNodeException>(async () =>
            await RerunMapBranchAsync(originalRunId, "map", 0, teamId, userId));
        ex.BlockedNodeIds.ShouldContain("se", "the side-effecting body node is named");

        (await RunCountAsync(teamId)).ShouldBe(before, "a side-effecting-body rerun must write nothing");
    }

    [Fact]
    public async Task Rerun_map_branch_with_a_suspendable_body_node_is_refused()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var probeKey = "mapbranch-suspbody-" + Guid.NewGuid().ToString("N");
        SuspendProbeNode.Reset(probeKey);
        var workflowId = await CreateWorkflowAsync(teamId, userId, SuspendableBodyMapDef(probeKey));
        // The original parks on the suspendable body — that's fine; gate 4 (static body scan) refuses regardless.
        var originalRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: FourElements);
        await RunEngineAsync(originalRunId);

        var before = await RunCountAsync(teamId);
        var ex = await Should.ThrowAsync<RerunBlockedByUnsupportedNodeException>(async () =>
            await RerunMapBranchAsync(originalRunId, "map", 0, teamId, userId));
        ex.BlockedNodeIds.ShouldContain("susp");

        (await RunCountAsync(teamId)).ShouldBe(before);
    }

    [Fact]
    public async Task Rerun_map_branch_with_a_nested_container_body_is_refused()
    {
        // A nested flow.map inside the target map's body is rerun-unsupported in v1 (the body scan refuses any
        // Map/Loop/Try child). The original need only EXIST — gate 4 runs before the map-succeeded gate.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, NestedMapInBranchDef());
        var originalRunId = await RunFreshAsync(workflowId, teamId, """{ "outer": ["a"], "inner": ["x"] }""");

        var before = await RunCountAsync(teamId);
        var ex = await Should.ThrowAsync<RerunBlockedByUnsupportedNodeException>(async () =>
            await RerunMapBranchAsync(originalRunId, "outer", 0, teamId, userId));
        ex.BlockedNodeIds.ShouldContain("inner", "the nested map in the branch body is named");

        (await RunCountAsync(teamId)).ShouldBe(before);
    }

    [Fact]
    public async Task Rerun_map_branch_for_a_run_in_another_team_is_not_found()
    {
        var (teamA, userA) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (teamB, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var key = "mapbranch-xteam-" + Guid.NewGuid().ToString("N");
        var workflowId = await CreateWorkflowAsync(teamA, userA, CountingMapDef(key));
        var originalRunId = await RunFreshAsync(workflowId, teamA, FourElements);

        var before = await RunCountAsync(teamB);
        await Should.ThrowAsync<KeyNotFoundException>(async () =>
            await RerunMapBranchAsync(originalRunId, "map", 0, teamB, userA));

        (await RunCountAsync(teamB)).ShouldBe(before);
    }

    [Fact]
    public async Task Rerun_map_branch_with_items_bound_to_a_live_re_resolved_scope_is_refused()
    {
        // The map binds items to {{wf.*}} — a scope that re-resolves LIVE on replay (so do project.* and
        // secret wf/team). The branch space could differ on rerun → reusing siblings by index would be unsound,
        // so it is refused. (Guards the silent wrong-element-attribution corruption.) Gate runs early, so the
        // original need only exist.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, LiveScopeItemsMapDef());
        var originalRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: "{}");
        await RunEngineAsync(originalRunId);

        var before = await RunCountAsync(teamId);
        await Should.ThrowAsync<RerunUpstreamNotReusableException>(async () =>
            await RerunMapBranchAsync(originalRunId, "map", 0, teamId, userId));

        (await RunCountAsync(teamId)).ShouldBe(before, "an items-binding to a live-re-resolved scope must write nothing");
    }

    [Fact]
    public async Task Rerun_the_last_map_branch_index()
    {
        // Boundary: rerun the highest valid index (N-1). Proves the index gate + seeder handle the edge.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var key = "mapbranch-last-" + Guid.NewGuid().ToString("N");
        var workflowId = await CreateWorkflowAsync(teamId, userId, CountingMapDef(key));
        var originalRunId = await RunFreshAsync(workflowId, teamId, FourElements);

        var rerunId = await RerunMapBranchAsync(originalRunId, "map", 3, teamId, userId);
        await RunEngineAsync(rerunId);

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);
        FlakyTestNode.AttemptsFor($"{key}-e0").ShouldBe(1);
        FlakyTestNode.AttemptsFor($"{key}-e3").ShouldBe(2, "the last branch re-ran exactly once");
        var results = await LoadMapResultsAsync(rerunId, "map");
        results[3].GetProperty("item").GetString().ShouldBe("e3");
        results[3].GetProperty("attempts").GetInt32().ShouldBe(2);
        results[0].GetProperty("attempts").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Rerun_the_only_branch_of_a_single_element_map()
    {
        // Degenerate: a 1-element map — rerun index 0 has NO siblings to reuse; the map re-enters and re-runs
        // the only branch. Proves branchCount==1 + the no-sibling seeding path.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var key = "mapbranch-solo-" + Guid.NewGuid().ToString("N");
        var workflowId = await CreateWorkflowAsync(teamId, userId, CountingMapDef(key));
        var originalRunId = await RunFreshAsync(workflowId, teamId, """{ "things": ["solo"] }""");
        FlakyTestNode.AttemptsFor($"{key}-solo").ShouldBe(1);

        var rerunId = await RerunMapBranchAsync(originalRunId, "map", 0, teamId, userId);
        await RunEngineAsync(rerunId);

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);
        FlakyTestNode.AttemptsFor($"{key}-solo").ShouldBe(2, "the only branch re-ran exactly once");
        var results = await LoadMapResultsAsync(rerunId, "map");
        results.GetArrayLength().ShouldBe(1);
        results[0].GetProperty("item").GetString().ShouldBe("solo");
        results[0].GetProperty("attempts").GetInt32().ShouldBe(2);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Definition builders
    // ─────────────────────────────────────────────────────────────────────────────

    // start → map(items={{trigger.things}}; body: ms → flaky[per-element counter] → echo[carries {{item}} + the count])
    //   → synth(reads results) → end. The branch terminal `echo` records BOTH the element identity ({{item}}) and the
    // attempt count, so results[i].item proves element-order alignment at EVERY index (not just the re-run one).
    private static WorkflowDefinition CountingMapDef(string counterPrefix) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "map", TypeKey = "flow.map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "items": "{{trigger.things}}" }""") },
            new() { Id = "ms", TypeKey = "flow.map_start", ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "flaky", TypeKey = FlakyTestNode.Key, ParentId = "map",
                    Config = WorkflowsTestSeed.Json($$"""{ "key": "{{counterPrefix}}-{{"{{"}}item{{"}}"}}", "failTimes": 0 }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "echo", TypeKey = JsonEmitNode.Key, ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "item": "{{item}}", "attempts": "{{nodes.flaky.outputs.attempts}}" }""") },
            new() { Id = "synth", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "agg": "{{nodes.map.outputs.results}}" }""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "map" },
            new() { From = "map", To = "synth" },
            new() { From = "synth", To = "end" },
            new() { From = "ms", To = "flaky" },
            new() { From = "flaky", To = "echo" },
        },
    };

    // continue-mode map; body: ms → flaky[always fails, per-element key] → so every branch abandons but the map succeeds.
    private static WorkflowDefinition ContinueModeFailingMapDef(string counterPrefix) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "map", TypeKey = "flow.map", Config = WorkflowsTestSeed.Json("""{ "errorHandling": "continue" }"""), Inputs = WorkflowsTestSeed.Json("""{ "items": "{{trigger.things}}" }""") },
            new() { Id = "ms", TypeKey = "flow.map_start", ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "flaky", TypeKey = FlakyTestNode.Key, ParentId = "map",
                    Config = WorkflowsTestSeed.Json($$"""{ "key": "{{counterPrefix}}-{{"{{"}}item{{"}}"}}", "failTimes": 99 }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "failed": "{{nodes.map.outputs.failed}}" }""") },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "map" },
            new() { From = "map", To = "end" },
            new() { From = "ms", To = "flaky" },
        },
    };

    // terminate-mode map; body: ms → flaky[always fails] → the map FAILS (no clean aggregate).
    private static WorkflowDefinition TerminateModeFailingMapDef(string counterPrefix) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "map", TypeKey = "flow.map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "items": "{{trigger.things}}" }""") },
            new() { Id = "ms", TypeKey = "flow.map_start", ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "flaky", TypeKey = FlakyTestNode.Key, ParentId = "map",
                    Config = WorkflowsTestSeed.Json($$"""{ "key": "{{counterPrefix}}", "failTimes": 99 }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "map" },
            new() { From = "map", To = "end" },
            new() { From = "ms", To = "flaky" },
        },
    };

    // map binds items to {{wf.*}} — a live-re-resolved scope on replay → the items-determinism gate refuses.
    private static WorkflowDefinition LiveScopeItemsMapDef() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "map", TypeKey = "flow.map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "items": "{{wf.shards}}" }""") },
            new() { Id = "ms", TypeKey = "flow.map_start", ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "echo", TypeKey = JsonEmitNode.Key, ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "v": "{{item}}" }""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "map" },
            new() { From = "map", To = "end" },
            new() { From = "ms", To = "echo" },
        },
    };

    // map body contains a SIDE-EFFECTING node ("se" = MutatingProbe) → gate 4 refuses.
    private static WorkflowDefinition SideEffectingBodyMapDef(string probeKey) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "map", TypeKey = "flow.map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "items": "{{trigger.things}}" }""") },
            new() { Id = "ms", TypeKey = "flow.map_start", ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "se", TypeKey = MutatingProbeNode.Key, ParentId = "map", Config = WorkflowsTestSeed.Json($$"""{ "key": "{{probeKey}}-{{"{{"}}item{{"}}"}}" }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "map" },
            new() { From = "map", To = "end" },
            new() { From = "ms", To = "se" },
        },
    };

    // map body contains a CanSuspend node ("susp" = SuspendProbe) → gate 4 refuses.
    private static WorkflowDefinition SuspendableBodyMapDef(string probeKey) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "map", TypeKey = "flow.map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "items": "{{trigger.things}}" }""") },
            new() { Id = "ms", TypeKey = "flow.map_start", ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "susp", TypeKey = SuspendProbeNode.Key, ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json($$"""{ "key": "{{probeKey}}", "item": "{{"{{"}}item{{"}}"}}" }""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "map" },
            new() { From = "map", To = "end" },
            new() { From = "ms", To = "susp" },
        },
    };

    // outer map whose body contains a nested "inner" flow.map → gate 4 refuses (nested container in body).
    private static WorkflowDefinition NestedMapInBranchDef() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "outer", TypeKey = "flow.map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "items": "{{trigger.outer}}" }""") },
            new() { Id = "oms", TypeKey = "flow.map_start", ParentId = "outer", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "inner", TypeKey = "flow.map", ParentId = "outer", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "items": "{{trigger.inner}}" }""") },
            new() { Id = "ims", TypeKey = "flow.map_start", ParentId = "inner", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "iecho", TypeKey = JsonEmitNode.Key, ParentId = "inner", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "v": "{{item}}" }""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "outer" },
            new() { From = "outer", To = "end" },
            new() { From = "oms", To = "inner" },
            new() { From = "ims", To = "iecho" },
        },
    };

    // ─────────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────────────

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition def)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "mapbranch-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = def,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task<Guid> RunFreshAsync(Guid workflowId, Guid teamId, string payloadJson)
    {
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: payloadJson);
        await RunEngineAsync(runId);
        return runId;
    }

    private async Task<Guid> RerunMapBranchAsync(Guid originalRunId, string mapNodeId, int branchIndex, Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IWorkflowService>().RerunMapBranchAsync(originalRunId, mapNodeId, branchIndex, teamId, userId, CancellationToken.None);
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private async Task ForceEnqueuedAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().Database
            .ExecuteSqlInterpolatedAsync($"UPDATE workflow_run SET status = 'Enqueued' WHERE id = {runId}");
    }

    private async Task AssertRunStatusAsync(Guid runId, WorkflowRunStatus expected, string? because = null)
    {
        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.Status.ShouldBe(expected, $"{because} (run {runId}; error={run.Error})");
    }

    private async Task<Core.Persistence.Entities.WorkflowRunNode> LoadCellAsync(Guid runId, string nodeId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunNode.AsNoTracking()
            .SingleAsync(n => n.RunId == runId && n.NodeId == nodeId && n.IterationKey == "");
    }

    private async Task<JsonElement> LoadMapResultsAsync(Guid runId, string mapNodeId)
    {
        var cell = await LoadCellAsync(runId, mapNodeId);
        return JsonDocument.Parse(cell.OutputsJson).RootElement.GetProperty("results").Clone();
    }

    private async Task<int> NodeStartedCountAsync(Guid runId, string nodeId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunRecord.AsNoTracking()
            .CountAsync(r => r.RunId == runId && r.NodeId == nodeId && r.IterationKey == "" && r.RecordType == WorkflowRunRecordTypes.NodeStarted);
    }

    private async Task<int> BranchStartedCountAsync(Guid runId, string nodeId, string branchKey)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunRecord.AsNoTracking()
            .CountAsync(r => r.RunId == runId && r.NodeId == nodeId && r.IterationKey == branchKey && r.RecordType == WorkflowRunRecordTypes.NodeStarted);
    }

    private async Task<int> RunCountAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().CountAsync(r => r.TeamId == teamId);
    }
}
