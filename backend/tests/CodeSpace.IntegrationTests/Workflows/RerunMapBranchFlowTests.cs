using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Reconciliation;
using CodeSpace.Core.Services.Workflows.Rerun;
using CodeSpace.Core.Services.Workflows.RunSources;
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
/// while the target branch fires once more.</para>
///
/// <para>D7-5 lifts the pure-body-only restriction to a fail-closed ALLOWLIST (<see cref="RerunBranchBodyPolicy"/>):
/// a re-run branch body may be PURE, PURELY side-effecting (routes through the D7-3 approval gate at runtime —
/// approve → fire once / reject → skip, proven here with a per-element <see cref="MutatingProbeNode"/> counter), or
/// <c>agent.code</c> (re-stages a fresh AgentRun — proven in <c>RerunMapBranchAgentFlowTests</c>, the E2E tier). It
/// still REFUSES any other suspendable node (un-opted <see cref="SuspendProbeNode"/>), a BOTH side-effecting AND
/// suspendable node (<see cref="BothFlagsProbeNode"/>, the chat.post_message corruption guard), and a nested
/// container — each pinned. A real-registry drift-detector here fails the instant a new CanSuspend node author
/// forgets the opt-in.</para>
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

    // ─────────────────────────────────────────────────────────────────────────────
    //  Set primitive — RerunMapBranchesAsync (single-branch is the |set|==1 case)
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rerun_a_set_of_map_branches_reuses_the_rest_reruns_each_chosen_branch_and_re_synthesizes()
    {
        // The set primitive in ONE fork: rerun branches {1,3} of a 4-element succeeded map. Siblings 0/2 are REPLAYED
        // (counters unchanged, zero node.started); branches 1 AND 3 each re-run exactly once; the map re-aggregates in
        // element order; the downstream synthesizer re-runs once over the new aggregate.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var key = "mapbranch-set-" + Guid.NewGuid().ToString("N");

        var workflowId = await CreateWorkflowAsync(teamId, userId, CountingMapDef(key));
        var originalRunId = await RunFreshAsync(workflowId, teamId, FourElements);
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Success);

        var rerunId = await RerunMapBranchesAsync(originalRunId, "map", new HashSet<int> { 1, 3 }, teamId, userId);
        await RunEngineAsync(rerunId);

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);
        FlakyTestNode.AttemptsFor($"{key}-e0").ShouldBe(1, "sibling 0 was REPLAYED — its counter must not bump");
        FlakyTestNode.AttemptsFor($"{key}-e1").ShouldBe(2, "chosen branch 1 re-ran exactly once");
        FlakyTestNode.AttemptsFor($"{key}-e2").ShouldBe(1, "sibling 2 was replayed");
        FlakyTestNode.AttemptsFor($"{key}-e3").ShouldBe(2, "chosen branch 3 re-ran exactly once");

        (await BranchStartedCountAsync(rerunId, "echo", "map#0")).ShouldBe(0);
        (await BranchStartedCountAsync(rerunId, "echo", "map#1")).ShouldBe(1, "chosen branch 1 re-ran");
        (await BranchStartedCountAsync(rerunId, "echo", "map#2")).ShouldBe(0);
        (await BranchStartedCountAsync(rerunId, "echo", "map#3")).ShouldBe(1, "chosen branch 3 re-ran");

        var results = await LoadMapResultsAsync(rerunId, "map");
        results.GetArrayLength().ShouldBe(4);
        for (var i = 0; i < 4; i++)
            results[i].GetProperty("item").GetString().ShouldBe($"e{i}", $"results[{i}] holds element e{i} in order");
        results[0].GetProperty("attempts").GetInt32().ShouldBe(1);
        results[1].GetProperty("attempts").GetInt32().ShouldBe(2, "the re-run branch 1's fresh result slots back at index 1");
        results[2].GetProperty("attempts").GetInt32().ShouldBe(1);
        results[3].GetProperty("attempts").GetInt32().ShouldBe(2, "the re-run branch 3's fresh result slots back at index 3");

        (await NodeStartedCountAsync(rerunId, "synth")).ShouldBe(1, "the synthesizer re-ran once over the re-aggregated results");
    }

    [Fact]
    public async Task Rerun_map_branches_command_reruns_the_set_in_one_fork()
    {
        // The "Rerun all failed items" wire: the set command (RerunMapBranchesCommand) dispatched through the full
        // mediator pipeline forks ONE run reruning {0,2}; the other branches are reused. Proves the command + handler
        // + controller-bound BranchIndices reach the already-tested RerunMapBranchesAsync.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var key = "mapbranch-setcmd-" + Guid.NewGuid().ToString("N");

        var workflowId = await CreateWorkflowAsync(teamId, userId, CountingMapDef(key));
        var originalRunId = await RunFreshAsync(workflowId, teamId, FourElements);
        var before = await RunCountAsync(teamId);

        var rerunId = await RerunMapBranchesViaCommandAsync(originalRunId, "map", new[] { 0, 2 }, teamId, userId, Guid.NewGuid());
        await RunEngineAsync(rerunId);

        (await RunCountAsync(teamId)).ShouldBe(before + 1, "the set command forked exactly one run");
        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);
        FlakyTestNode.AttemptsFor($"{key}-e0").ShouldBe(2, "chosen branch 0 re-ran once");
        FlakyTestNode.AttemptsFor($"{key}-e1").ShouldBe(1, "sibling 1 was reused");
        FlakyTestNode.AttemptsFor($"{key}-e2").ShouldBe(2, "chosen branch 2 re-ran once");
        FlakyTestNode.AttemptsFor($"{key}-e3").ShouldBe(1, "sibling 3 was reused");
    }

    [Fact]
    public async Task A_rerun_fork_threads_its_lineage_back_to_the_original_via_parent_run_id()
    {
        // The run-detail DTO exposes ParentRunId + SourceType so the UI can thread a fork back to the run it reran
        // ("Rerun of {original}") — the fork reads as that run's re-run, not a stray new run.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var key = "maplineage-" + Guid.NewGuid().ToString("N");

        var workflowId = await CreateWorkflowAsync(teamId, userId, CountingMapDef(key));
        var originalRunId = await RunFreshAsync(workflowId, teamId, FourElements);

        var forkId = await RerunMapBranchesAsync(originalRunId, "map", new HashSet<int> { 0 }, teamId, userId);

        using var scope = _fixture.BeginScope();
        var detail = await scope.Resolve<IWorkflowService>().GetRunAsync(forkId, teamId, CancellationToken.None);
        detail.ShouldNotBeNull();
        detail!.ParentRunId.ShouldBe(originalRunId, "the rerun fork points back to the original run");
        detail.SourceType.ShouldBe(WorkflowRunSourceTypes.Rerun);

        var original = await scope.Resolve<IWorkflowService>().GetRunAsync(originalRunId, teamId, CancellationToken.None);
        original!.ParentRunId.ShouldBeNull("the original run has no parent");
    }

    [Fact]
    public async Task Rerun_the_full_branch_set_reruns_every_branch()
    {
        // The whole set {0,1,2,3} → every branch re-runs (nothing reused); the map re-aggregates over all-fresh results.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var key = "mapbranch-full-" + Guid.NewGuid().ToString("N");

        var workflowId = await CreateWorkflowAsync(teamId, userId, CountingMapDef(key));
        var originalRunId = await RunFreshAsync(workflowId, teamId, FourElements);

        var rerunId = await RerunMapBranchesAsync(originalRunId, "map", new HashSet<int> { 0, 1, 2, 3 }, teamId, userId);
        await RunEngineAsync(rerunId);

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);
        for (var i = 0; i < 4; i++)
        {
            FlakyTestNode.AttemptsFor($"{key}-e{i}").ShouldBe(2, $"branch {i} re-ran (the whole set was chosen)");
            (await BranchStartedCountAsync(rerunId, "echo", $"map#{i}")).ShouldBe(1, $"branch {i} re-ran on the fork");
        }
    }

    [Fact]
    public async Task Rerun_an_empty_branch_set_is_refused_and_writes_nothing()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var key = "mapbranch-empty-" + Guid.NewGuid().ToString("N");
        var workflowId = await CreateWorkflowAsync(teamId, userId, CountingMapDef(key));
        var originalRunId = await RunFreshAsync(workflowId, teamId, FourElements);

        var before = await RunCountAsync(teamId);
        await Should.ThrowAsync<RerunTargetNotFoundException>(async () =>
            await RerunMapBranchesAsync(originalRunId, "map", new HashSet<int>(), teamId, userId));
        (await RunCountAsync(teamId)).ShouldBe(before, "an empty branch set must write no fork");
    }

    [Fact]
    public async Task Rerun_a_set_with_an_out_of_range_index_is_refused_and_writes_nothing()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var key = "mapbranch-setrange-" + Guid.NewGuid().ToString("N");
        var workflowId = await CreateWorkflowAsync(teamId, userId, CountingMapDef(key));
        var originalRunId = await RunFreshAsync(workflowId, teamId, FourElements);

        var before = await RunCountAsync(teamId);
        await Should.ThrowAsync<RerunTargetNotFoundException>(async () =>
            await RerunMapBranchesAsync(originalRunId, "map", new HashSet<int> { 1, 99 }, teamId, userId));   // valid indices 0..3
        (await RunCountAsync(teamId)).ShouldBe(before, "any out-of-range index in the set rejects the whole rerun — write nothing");
    }

    [Fact]
    public async Task Rerun_a_set_in_continue_mode_replays_the_unchosen_failed_siblings_without_re_firing_them()
    {
        // Continue-mode map where every branch abandons → Success with failed=4. Rerun the SET {0,2}: the UNCHOSEN
        // abandoned siblings 1/3 are REPLAYED (their failing node never re-fires — the #302 guard at set cardinality),
        // branches 0 AND 2 re-run (and abandon again), and the failed count is reconstructed over the mix.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var key = "mapbranch-set-continue-" + Guid.NewGuid().ToString("N");

        var workflowId = await CreateWorkflowAsync(teamId, userId, ContinueModeFailingMapDef(key));
        var originalRunId = await RunFreshAsync(workflowId, teamId, FourElements);
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Success);

        var rerunId = await RerunMapBranchesAsync(originalRunId, "map", new HashSet<int> { 0, 2 }, teamId, userId);
        await RunEngineAsync(rerunId);

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);
        FlakyTestNode.AttemptsFor($"{key}-e0").ShouldBe(2, "chosen branch 0 re-ran (and abandoned again)");
        FlakyTestNode.AttemptsFor($"{key}-e1").ShouldBe(1, "replayed abandoned sibling 1 MUST NOT re-fire its failing node");
        FlakyTestNode.AttemptsFor($"{key}-e2").ShouldBe(2, "chosen branch 2 re-ran");
        FlakyTestNode.AttemptsFor($"{key}-e3").ShouldBe(1, "replayed abandoned sibling 3 MUST NOT re-fire");

        JsonDocument.Parse((await LoadCellAsync(rerunId, "map")).OutputsJson).RootElement.GetProperty("failed").GetInt32()
            .ShouldBe(4, "the reconstructed aggregate preserves the continue-mode failed count across the set rerun");
    }

    [Fact]
    public async Task Rerun_a_set_re_walk_before_completion_does_not_re_execute_siblings_or_re_fire_chosen_branches()
    {
        // Durability for a SET: a spurious re-dispatch of the terminal fork must re-execute NEITHER a replayed sibling
        // NOR either chosen branch — exactly-once-per-branch holds across the seeded-replay path for a multi-hole set.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var key = "mapbranch-set-rewalk-" + Guid.NewGuid().ToString("N");

        var workflowId = await CreateWorkflowAsync(teamId, userId, CountingMapDef(key));
        var originalRunId = await RunFreshAsync(workflowId, teamId, FourElements);

        var rerunId = await RerunMapBranchesAsync(originalRunId, "map", new HashSet<int> { 1, 3 }, teamId, userId);
        await RunEngineAsync(rerunId);
        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);

        await ForceEnqueuedAsync(rerunId);
        await RunEngineAsync(rerunId);

        FlakyTestNode.AttemptsFor($"{key}-e0").ShouldBe(1, "replayed sibling 0 not re-run by the re-walk");
        FlakyTestNode.AttemptsFor($"{key}-e1").ShouldBe(2, "chosen branch 1 re-ran exactly once total — the re-walk did not bump it again");
        FlakyTestNode.AttemptsFor($"{key}-e2").ShouldBe(1);
        FlakyTestNode.AttemptsFor($"{key}-e3").ShouldBe(2, "chosen branch 3 re-ran exactly once total");
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

    // ─────────────────────────────────────────────────────────────────────────────
    //  Terminate-mode rerun — the relaxed gate admits a terminate-failed map; the
    //  TrySettleBranch Terminate arm replays an unchosen failed sibling as failed.
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rerun_all_failed_branches_of_a_terminate_map_that_now_pass_makes_the_map_succeed()
    {
        // The screenshot's case: a terminate-mode fan-out where EVERY branch failed (the map node is Failure). The
        // relaxed gate admits it. Rerun ALL branches with a node that fails ONCE (failTimes=1) → each re-runs at
        // attempt 2 and now succeeds → the map re-aggregates to Success and the downstream synthesizer runs.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var key = "mapterm-all-" + Guid.NewGuid().ToString("N");

        var workflowId = await CreateWorkflowAsync(teamId, userId, TerminateFailingMapDef(key, failTimes: 1));
        var originalRunId = await RunFreshAsync(workflowId, teamId, FourElements);
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Failure, "every branch fails its first attempt → terminate");
        for (var i = 0; i < 4; i++) FlakyTestNode.AttemptsFor($"{key}-e{i}").ShouldBe(1);

        var rerunId = await RerunMapBranchesAsync(originalRunId, "map", new HashSet<int> { 0, 1, 2, 3 }, teamId, userId);
        await RunEngineAsync(rerunId);

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success, "all branches re-ran and passed on attempt 2 → the map succeeds");
        for (var i = 0; i < 4; i++) FlakyTestNode.AttemptsFor($"{key}-e{i}").ShouldBe(2, $"branch {i} re-ran exactly once");
        (await NodeStartedCountAsync(rerunId, "synth")).ShouldBe(1, "the synthesizer ran once the map succeeded");
        (await LoadMapResultsAsync(rerunId, "map")).GetArrayLength().ShouldBe(4);
    }

    [Fact]
    public async Task Rerun_one_of_several_failed_terminate_branches_leaves_the_others_failed_and_the_map_blocked()
    {
        // The DIAGNOSTIC case + the degrade guard. A terminate map where every branch always fails (failTimes=99).
        // Rerun ONLY branch 0: it re-runs (and fails again), while the UNCHOSEN failed siblings 1/2/3 are REPLAYED as
        // terminate-failures (their node never re-fires) — so the map RE-AGGREGATES TO FAILURE (not silently Success,
        // the TerminateFailure-not-just-Failed pin) and the downstream synthesizer stays BLOCKED.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var key = "mapterm-diag-" + Guid.NewGuid().ToString("N");

        var workflowId = await CreateWorkflowAsync(teamId, userId, TerminateFailingMapDef(key, failTimes: 99));
        var originalRunId = await RunFreshAsync(workflowId, teamId, FourElements);
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Failure);

        var rerunId = await RerunMapBranchesAsync(originalRunId, "map", new HashSet<int> { 0 }, teamId, userId);
        await RunEngineAsync(rerunId);

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Failure,
            "branch 0 re-ran but still failed AND siblings 1/2/3 replay terminate-failed → the map stays Failure (no degrade to Success)");
        FlakyTestNode.AttemptsFor($"{key}-e0").ShouldBe(2, "chosen branch 0 re-ran (and failed again)");
        FlakyTestNode.AttemptsFor($"{key}-e1").ShouldBe(1, "unchosen failed sibling 1 was REPLAYED — its failing node must NOT re-fire");
        FlakyTestNode.AttemptsFor($"{key}-e2").ShouldBe(1);
        FlakyTestNode.AttemptsFor($"{key}-e3").ShouldBe(1);
        (await NodeStartedCountAsync(rerunId, "synth")).ShouldBe(0, "the map stayed Failure → the downstream synthesizer is blocked");
    }

    [Fact]
    public async Task Rerun_the_failed_branch_of_a_partially_failed_terminate_map_reuses_the_succeeded_siblings_and_succeeds()
    {
        // The most realistic operator case: a terminate map where branch 0 FAILED but 1/2/3 SUCCEEDED → map Failure.
        // Rerun {0} (it now passes on attempt 2): the succeeded siblings 1/2/3 are REUSED (not re-run, via the
        // unchanged Success replay arm), branch 0 re-runs and passes → the map re-aggregates to Success.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var key = "mapterm-partial-" + Guid.NewGuid().ToString("N");

        var workflowId = await CreateWorkflowAsync(teamId, userId, ElementControlledTerminateMapDef(key));
        // e0 fails once (ft:1) then passes; e1/e2/e3 always pass (ft:0).
        var originalRunId = await RunFreshAsync(workflowId, teamId, """{ "things": [ {"v":"e0","ft":1}, {"v":"e1","ft":0}, {"v":"e2","ft":0}, {"v":"e3","ft":0} ] }""");
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Failure, "branch 0 fails its first attempt → terminate, even though 1/2/3 succeeded");
        FlakyTestNode.AttemptsFor($"{key}-e0").ShouldBe(1);
        for (var i = 1; i < 4; i++) FlakyTestNode.AttemptsFor($"{key}-e{i}").ShouldBe(1, $"sibling {i} ran + succeeded on the original");

        var rerunId = await RerunMapBranchesAsync(originalRunId, "map", new HashSet<int> { 0 }, teamId, userId);
        await RunEngineAsync(rerunId);

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success, "branch 0 re-ran and passed; the succeeded siblings reused → the map succeeds");
        FlakyTestNode.AttemptsFor($"{key}-e0").ShouldBe(2, "branch 0 re-ran once and passed");
        for (var i = 1; i < 4; i++) FlakyTestNode.AttemptsFor($"{key}-e{i}").ShouldBe(1, $"succeeded sibling {i} was REUSED — not re-run");
        (await NodeStartedCountAsync(rerunId, "synth")).ShouldBe(1, "the synthesizer ran once the map succeeded");
        (await LoadMapResultsAsync(rerunId, "map")).GetArrayLength().ShouldBe(4);
    }

    [Fact]
    public async Task Rerun_a_terminate_map_re_walk_before_completion_does_not_re_fire_chosen_branch_or_replayed_siblings()
    {
        // Durability for the terminate path: a spurious re-dispatch of the (still-Failure) terminate-rerun fork must
        // re-fire NEITHER the chosen re-run branch NOR the replayed terminate-failed siblings.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var key = "mapterm-rewalk-" + Guid.NewGuid().ToString("N");

        var workflowId = await CreateWorkflowAsync(teamId, userId, TerminateFailingMapDef(key, failTimes: 99));
        var originalRunId = await RunFreshAsync(workflowId, teamId, FourElements);

        var rerunId = await RerunMapBranchesAsync(originalRunId, "map", new HashSet<int> { 0 }, teamId, userId);
        await RunEngineAsync(rerunId);
        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Failure);

        await ForceEnqueuedAsync(rerunId);
        await RunEngineAsync(rerunId);

        FlakyTestNode.AttemptsFor($"{key}-e0").ShouldBe(2, "the chosen branch re-ran exactly once total — the re-walk did not bump it again");
        FlakyTestNode.AttemptsFor($"{key}-e1").ShouldBe(1, "replayed terminate-failed sibling 1 stayed put");
        FlakyTestNode.AttemptsFor($"{key}-e2").ShouldBe(1);
        FlakyTestNode.AttemptsFor($"{key}-e3").ShouldBe(1);
    }

    [Fact]
    public async Task Rerun_a_branch_of_a_map_that_failed_before_fan_out_is_refused()
    {
        // A map-OWN failure (items resolved to a non-array → the map fails BEFORE any branch dispatched) leaves zero
        // branch rows, so it is refused (by the branch-count guard, not the relaxed status gate) — the relaxed gate
        // admits Failure, but only a genuine branch-terminate Failure has branches to re-run.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var key = "mapterm-binding-" + Guid.NewGuid().ToString("N");
        var workflowId = await CreateWorkflowAsync(teamId, userId, NonArrayItemsMapDef());
        var originalRunId = await RunFreshAsync(workflowId, teamId, """{ "scalar": "not-an-array" }""");
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Failure, "a non-array items binding fails the map before fan-out");

        var before = await RunCountAsync(teamId);
        await Should.ThrowAsync<RerunTargetNotFoundException>(async () =>
            await RerunMapBranchAsync(originalRunId, "map", 0, teamId, userId));
        (await RunCountAsync(teamId)).ShouldBe(before, "a map that failed before fan-out has no branch to re-run — write nothing");
    }

    [Fact]
    public async Task Rerun_a_branch_of_a_terminate_map_whose_siblings_recovered_via_an_in_body_error_edge_reuses_them_and_stays_successful()
    {
        // The boundary the terminate arm must NOT cross: a body node that FAILS but is HANDLED by its own in-body
        // error edge is NOT a terminate point — the branch recovers and the terminate-mode map SUCCEEDS. Rerunning
        // one branch must reuse the error-edge-recovered siblings (their flaky never re-fires) and stay Success —
        // proving the !HasErrorEdgeInDefinition guard keeps a handled failure out of the terminate settlement.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var key = "mapterm-edge-" + Guid.NewGuid().ToString("N");

        var workflowId = await CreateWorkflowAsync(teamId, userId, EdgeHandledTerminateMapDef(key));
        var originalRunId = await RunFreshAsync(workflowId, teamId, FourElements);
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Success,
            "every branch's flaky failed but its error edge recovered to a succeeding node → no terminate, the map succeeds");
        for (var i = 0; i < 4; i++) FlakyTestNode.AttemptsFor($"{key}-e{i}").ShouldBe(1, $"branch {i} ran once + recovered via its error edge");

        var rerunId = await RerunMapBranchesAsync(originalRunId, "map", new HashSet<int> { 0 }, teamId, userId);
        await RunEngineAsync(rerunId);

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success, "branch 0 re-ran and recovered again; the recovered siblings reused → the map stays Success");
        FlakyTestNode.AttemptsFor($"{key}-e0").ShouldBe(2, "branch 0 re-ran once and recovered again");
        for (var i = 1; i < 4; i++) FlakyTestNode.AttemptsFor($"{key}-e{i}").ShouldBe(1, $"recovered sibling {i} was REUSED — its handled failure must NOT re-fire");
        (await NodeStartedCountAsync(rerunId, "synth")).ShouldBe(1, "the synthesizer ran once the map succeeded");
        (await LoadMapResultsAsync(rerunId, "map")).GetArrayLength().ShouldBe(4);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  OperationId idempotency — a double-submit / HTTP retry of the SAME rerun click
    //  (same client-minted token) returns the SAME fork; it never forks twice.
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rerun_with_the_same_operation_id_twice_returns_the_same_fork_and_never_forks_again()
    {
        // The double-submit / HTTP-retry case: the client mints ONE token and (re)sends it. The first call forks; the
        // second resolves to the SAME fork WITHOUT a second run and WITHOUT re-seeding — and the fork is the real one
        // (executing it re-runs ONLY the chosen branch, exactly once, reusing the rest).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var key = "mapidem-same-" + Guid.NewGuid().ToString("N");

        var workflowId = await CreateWorkflowAsync(teamId, userId, CountingMapDef(key));
        var originalRunId = await RunFreshAsync(workflowId, teamId, FourElements);
        for (var i = 0; i < 4; i++) FlakyTestNode.AttemptsFor($"{key}-e{i}").ShouldBe(1);

        var opId = Guid.NewGuid();
        var beforeReruns = await RunCountAsync(teamId);

        var fork1 = await RerunMapBranchesAsync(originalRunId, "map", new HashSet<int> { 0 }, teamId, userId, opId);
        var fork2 = await RerunMapBranchesAsync(originalRunId, "map", new HashSet<int> { 0 }, teamId, userId, opId);

        fork2.ShouldBe(fork1, "the second submit of the same operation id returns the SAME fork");
        (await RunCountAsync(teamId)).ShouldBe(beforeReruns + 1, "the duplicate submit created NO second fork");

        await RunEngineAsync(fork1);
        await AssertRunStatusAsync(fork1, WorkflowRunStatus.Success);
        FlakyTestNode.AttemptsFor($"{key}-e0").ShouldBe(2, "the chosen branch re-ran EXACTLY once despite the double-submit");
        for (var i = 1; i < 4; i++) FlakyTestNode.AttemptsFor($"{key}-e{i}").ShouldBe(1, $"sibling {i} was reused — not re-run");
        (await LoadMapResultsAsync(fork1, "map")).GetArrayLength().ShouldBe(4);
    }

    [Fact]
    public async Task Rerun_with_distinct_operation_ids_forks_each_time()
    {
        // Two genuine clicks mint two tokens → two independent forks (idempotency is per-token, never coalescing
        // distinct operations).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var key = "mapidem-distinct-" + Guid.NewGuid().ToString("N");

        var workflowId = await CreateWorkflowAsync(teamId, userId, CountingMapDef(key));
        var originalRunId = await RunFreshAsync(workflowId, teamId, FourElements);
        var beforeReruns = await RunCountAsync(teamId);

        var fork1 = await RerunMapBranchesAsync(originalRunId, "map", new HashSet<int> { 0 }, teamId, userId, Guid.NewGuid());
        var fork2 = await RerunMapBranchesAsync(originalRunId, "map", new HashSet<int> { 1 }, teamId, userId, Guid.NewGuid());

        fork2.ShouldNotBe(fork1, "distinct operation ids fork independently");
        (await RunCountAsync(teamId)).ShouldBe(beforeReruns + 2, "each distinct-token submit created its own fork");
    }

    [Fact]
    public async Task Rerun_without_an_operation_id_forks_independently_once_the_prior_fork_completes()
    {
        // Opt-in: a client that sends NO token gets no idempotency — a fresh rerun of the same branch forks again
        // ONCE the prior one finished (its lease released inline on completion). Two token-less reruns of the same
        // branch are NOT coalesced (no dedup); they're merely serialized by the active-rerun lease.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var key = "mapidem-null-" + Guid.NewGuid().ToString("N");

        var workflowId = await CreateWorkflowAsync(teamId, userId, CountingMapDef(key));
        var originalRunId = await RunFreshAsync(workflowId, teamId, FourElements);
        var beforeReruns = await RunCountAsync(teamId);

        var fork1 = await RerunMapBranchesAsync(originalRunId, "map", new HashSet<int> { 0 }, teamId, userId, operationId: null);
        await RunEngineAsync(fork1);   // fork1 completes → its branch-0 lease is released inline

        var fork2 = await RerunMapBranchesAsync(originalRunId, "map", new HashSet<int> { 0 }, teamId, userId, operationId: null);

        fork2.ShouldNotBe(fork1, "no token → no dedup → a fresh rerun of the same branch forks again");
        (await RunCountAsync(teamId)).ShouldBe(beforeReruns + 2, "both token-less submits forked (the lease released between them)");
    }

    [Fact]
    public async Task Rerun_command_dispatched_twice_with_the_same_operation_id_returns_one_fork()
    {
        // The FAITHFUL production path: dispatch the command through the mediator so the TransactionalBehavior wraps
        // it in one DB transaction. The duplicate's 23505 must roll back to EF's per-SaveChanges savepoint and leave
        // the transaction usable for the dedup LOOKUP — proving the savepoint reasoning, not just the autocommit case.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var key = "mapidem-cmd-" + Guid.NewGuid().ToString("N");

        var workflowId = await CreateWorkflowAsync(teamId, userId, CountingMapDef(key));
        var originalRunId = await RunFreshAsync(workflowId, teamId, FourElements);

        var opId = Guid.NewGuid();
        var beforeReruns = await RunCountAsync(teamId);

        var fork1 = await RerunMapBranchViaCommandAsync(originalRunId, "map", 0, teamId, userId, opId);
        var fork2 = await RerunMapBranchViaCommandAsync(originalRunId, "map", 0, teamId, userId, opId);

        fork2.ShouldBe(fork1, "the duplicate command (same token) returns the prior fork through the full mediator pipeline");
        (await RunCountAsync(teamId)).ShouldBe(beforeReruns + 1, "no second fork via the transactional command path");
    }

    [Fact]
    public async Task Concurrent_rerun_submits_with_the_same_operation_id_create_exactly_one_fork()
    {
        // The race: two CONCURRENT submits (separate scopes / connections / transactions) carrying the same token.
        // The partial unique index serializes them — one inserts + commits, the other blocks then raises 23505 and
        // resolves the winner's fork. Exactly one fork; both callers get the same id. No lease needed for THIS guard.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var key = "mapidem-race-" + Guid.NewGuid().ToString("N");

        var workflowId = await CreateWorkflowAsync(teamId, userId, CountingMapDef(key));
        var originalRunId = await RunFreshAsync(workflowId, teamId, FourElements);

        var opId = Guid.NewGuid();
        var beforeReruns = await RunCountAsync(teamId);

        var submitA = RerunMapBranchViaCommandAsync(originalRunId, "map", 0, teamId, userId, opId);
        var submitB = RerunMapBranchViaCommandAsync(originalRunId, "map", 0, teamId, userId, opId);
        var forks = await Task.WhenAll(submitA, submitB);

        forks[0].ShouldBe(forks[1], "both concurrent submits of the same operation id resolve to ONE fork");
        (await RunCountAsync(teamId)).ShouldBe(beforeReruns + 1, "exactly one fork was created despite two concurrent submits");
    }

    [Fact]
    public async Task Rerun_a_snapshot_run_map_branch_with_the_same_operation_id_twice_returns_one_fork()
    {
        // The OTHER fork path: a snapshot-origin run (WorkflowId null) forks through RunFromSnapshotStarter, whose
        // 23505 → detach → Guid.Empty short-circuit was hand-written this slice (the authored RunStarter mirror). A
        // double-submit must dedup there too — exactly one fork, the chosen branch re-runs once, siblings reused.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var key = "mapidem-snap-" + Guid.NewGuid().ToString("N");

        var originalRunId = await RunSnapshotFreshAsync(CountingMapDef(key), teamId, userId, FourElements);
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Success, "the snapshot map ran fresh");
        for (var i = 0; i < 4; i++) FlakyTestNode.AttemptsFor($"{key}-e{i}").ShouldBe(1);

        var opId = Guid.NewGuid();
        var beforeReruns = await RunCountAsync(teamId);

        var fork1 = await RerunMapBranchesAsync(originalRunId, "map", new HashSet<int> { 0 }, teamId, userId, opId);
        var fork2 = await RerunMapBranchesAsync(originalRunId, "map", new HashSet<int> { 0 }, teamId, userId, opId);

        fork2.ShouldBe(fork1, "the snapshot-fork dedup returns the SAME fork");
        (await RunCountAsync(teamId)).ShouldBe(beforeReruns + 1, "the duplicate snapshot-fork submit created no second fork");

        await RunEngineAsync(fork1);
        await AssertRunStatusAsync(fork1, WorkflowRunStatus.Success);
        FlakyTestNode.AttemptsFor($"{key}-e0").ShouldBe(2, "the chosen branch re-ran EXACTLY once despite the double-submit");
        for (var i = 1; i < 4; i++) FlakyTestNode.AttemptsFor($"{key}-e{i}").ShouldBe(1, $"sibling {i} was reused — not re-run");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Active-rerun lease — a DISTINCT-operation concurrent rerun of the SAME branch
    //  is refused (409) while the first is in flight; it frees the instant that fork
    //  ends, and the reconciler's terminal-join sweep is the complete backstop.
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rerun_a_branch_already_being_reran_is_refused_then_allowed_once_the_first_fork_completes()
    {
        // The core lease guarantee + the inline release. A distinct-token rerun of a branch whose prior rerun is
        // still in flight is refused with RerunAlreadyInProgressException (→ 409) and its fork rolls back (no orphan).
        // Once the first fork reaches terminal, its lease frees inline → the same branch can be reran again.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var key = "maplease-serial-" + Guid.NewGuid().ToString("N");

        var workflowId = await CreateWorkflowAsync(teamId, userId, CountingMapDef(key));
        var originalRunId = await RunFreshAsync(workflowId, teamId, FourElements);

        var fork1 = await RerunMapBranchViaCommandAsync(originalRunId, "map", 0, teamId, userId, Guid.NewGuid());   // lease held (fork1 not executed)
        (await ActiveLeaseCountAsync(originalRunId)).ShouldBe(1, "the first rerun holds an in-progress lease on branch 0");

        var afterFirst = await RunCountAsync(teamId);
        await Should.ThrowAsync<RerunAlreadyInProgressException>(() =>
            RerunMapBranchViaCommandAsync(originalRunId, "map", 0, teamId, userId, Guid.NewGuid()));   // distinct token, same branch → 409
        (await RunCountAsync(teamId)).ShouldBe(afterFirst, "the refused rerun's fork rolled back with its transaction — no orphan");

        await RunEngineAsync(fork1);   // fork1 completes → its lease releases inline
        (await ActiveLeaseCountAsync(originalRunId)).ShouldBe(0, "the lease freed the instant the fork reached terminal");

        var fork3 = await RerunMapBranchViaCommandAsync(originalRunId, "map", 0, teamId, userId, Guid.NewGuid());   // now allowed
        fork3.ShouldNotBe(fork1, "once the lease released, the same branch reruns again as a fresh fork");
    }

    [Fact]
    public async Task Concurrent_distinct_token_reruns_of_the_same_branch_admit_exactly_one()
    {
        // The race: two CONCURRENT distinct-token reruns of the SAME branch. The partial-unique lease serializes
        // them — one acquires + forks, the other loses the lease insert (23505 → RerunAlreadyInProgressException) and
        // rolls back. Exactly one fork; exactly one rejection. (OperationId can't help — the tokens differ.)
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var key = "maplease-race-" + Guid.NewGuid().ToString("N");

        var workflowId = await CreateWorkflowAsync(teamId, userId, CountingMapDef(key));
        var originalRunId = await RunFreshAsync(workflowId, teamId, FourElements);
        var before = await RunCountAsync(teamId);

        async Task<Exception?> TryRerun(Guid op)
        {
            try { await RerunMapBranchViaCommandAsync(originalRunId, "map", 0, teamId, userId, op); return null; }
            catch (Exception ex) { return ex; }
        }

        var errors = (await Task.WhenAll(TryRerun(Guid.NewGuid()), TryRerun(Guid.NewGuid()))).Where(e => e != null).ToList();

        errors.Count.ShouldBe(1, "exactly one of the two concurrent reruns was refused");
        errors[0].ShouldBeOfType<RerunAlreadyInProgressException>();
        (await RunCountAsync(teamId)).ShouldBe(before + 1, "exactly one fork survived; the loser rolled back");
        (await ActiveLeaseCountAsync(originalRunId)).ShouldBe(1, "the single winner holds the only in-progress lease");
    }

    [Fact]
    public async Task Reruns_of_disjoint_branches_do_not_conflict()
    {
        // Disjoint branch sets never collide: two in-flight reruns of DIFFERENT branches both hold their own leases.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var key = "maplease-disjoint-" + Guid.NewGuid().ToString("N");

        var workflowId = await CreateWorkflowAsync(teamId, userId, CountingMapDef(key));
        var originalRunId = await RunFreshAsync(workflowId, teamId, FourElements);
        var before = await RunCountAsync(teamId);

        var fork1 = await RerunMapBranchesAsync(originalRunId, "map", new HashSet<int> { 0 }, teamId, userId, Guid.NewGuid());
        var fork2 = await RerunMapBranchesAsync(originalRunId, "map", new HashSet<int> { 1 }, teamId, userId, Guid.NewGuid());   // disjoint → no conflict

        fork2.ShouldNotBe(fork1, "disjoint-branch reruns fork independently");
        (await RunCountAsync(teamId)).ShouldBe(before + 2, "both disjoint reruns forked");
        (await ActiveLeaseCountAsync(originalRunId)).ShouldBe(2, "each holds its own branch lease");
    }

    [Fact]
    public async Task Same_token_retry_of_an_in_flight_rerun_dedups_and_does_not_trip_the_lease()
    {
        // The ordering hazard, resolved: a SAME-token retry must dedup to the prior fork (OperationId) and must NOT
        // be misread as a lease conflict, even though the prior fork's lease is still held. The dedup short-circuit
        // runs BEFORE the lease is taken.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var key = "maplease-sametoken-" + Guid.NewGuid().ToString("N");

        var workflowId = await CreateWorkflowAsync(teamId, userId, CountingMapDef(key));
        var originalRunId = await RunFreshAsync(workflowId, teamId, FourElements);

        var opId = Guid.NewGuid();
        var fork1 = await RerunMapBranchesAsync(originalRunId, "map", new HashSet<int> { 0 }, teamId, userId, opId);   // lease held, fork1 not executed
        var before = await RunCountAsync(teamId);

        var fork2 = await RerunMapBranchesAsync(originalRunId, "map", new HashSet<int> { 0 }, teamId, userId, opId);   // SAME token → dedup, NOT a 409

        fork2.ShouldBe(fork1, "a same-token retry dedups to the prior fork — it must NOT hit the held lease as a false conflict");
        (await RunCountAsync(teamId)).ShouldBe(before, "no second fork; the retry returned the prior one");
        (await ActiveLeaseCountAsync(originalRunId)).ShouldBe(1, "still exactly one lease — the retry took none");
    }

    [Fact]
    public async Task A_rerun_set_that_overlaps_an_in_flight_lease_on_any_branch_is_refused_atomically()
    {
        // SET granularity: the per-branch lease catches an overlap on ANY branch of the set, and the conflicting
        // insert is ATOMIC — a {1,2} rerun that collides with an in-flight {0,1} on branch 1 is refused WHOLE (it
        // leaks no partial branch-2 lease). (Driven through the service for a multi-branch set; the single-branch
        // mediator tests already prove the fork rolls back on the throw.)
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var key = "maplease-setoverlap-" + Guid.NewGuid().ToString("N");

        var workflowId = await CreateWorkflowAsync(teamId, userId, CountingMapDef(key));
        var originalRunId = await RunFreshAsync(workflowId, teamId, FourElements);

        await RerunMapBranchesAsync(originalRunId, "map", new HashSet<int> { 0, 1 }, teamId, userId, Guid.NewGuid());   // holds leases on 0 and 1
        (await ActiveLeaseCountAsync(originalRunId)).ShouldBe(2, "the first rerun holds leases on branches 0 and 1");

        await Should.ThrowAsync<RerunAlreadyInProgressException>(() =>
            RerunMapBranchesAsync(originalRunId, "map", new HashSet<int> { 1, 2 }, teamId, userId, Guid.NewGuid()));   // overlaps on branch 1

        (await ActiveLeaseCountAsync(originalRunId)).ShouldBe(2, "the refused set leaked no partial lease — its branch-2 row never committed");
    }

    [Fact]
    public async Task Reconciler_releases_the_lease_of_a_fork_cancelled_outside_the_completion_path()
    {
        // The reaper backstop. An operator cancel flips the fork to Cancelled via a CAS that BYPASSES the engine's
        // inline release, so the lease lingers in_progress. The reconciler's terminal-join sweep frees it (the fork
        // is terminal), after which the branch can be reran again.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var key = "maplease-reaper-" + Guid.NewGuid().ToString("N");

        var workflowId = await CreateWorkflowAsync(teamId, userId, CountingMapDef(key));
        var originalRunId = await RunFreshAsync(workflowId, teamId, FourElements);

        var fork1 = await RerunMapBranchesAsync(originalRunId, "map", new HashSet<int> { 0 }, teamId, userId, Guid.NewGuid());
        await CancelRunAsync(fork1, teamId);   // Cancelled via CAS — the inline release does NOT run
        await AssertRunStatusAsync(fork1, WorkflowRunStatus.Cancelled);
        (await ActiveLeaseCountAsync(originalRunId)).ShouldBe(1, "the cancel bypassed the inline release — the lease lingers");

        var summary = await ReconcileAsync();

        summary.ReleasedRerunLeases.ShouldBeGreaterThanOrEqualTo(1, "the reconciler's terminal-join freed the cancelled fork's lease");
        (await ActiveLeaseCountAsync(originalRunId)).ShouldBe(0, "no in-progress lease remains for this run");

        var fork2 = await RerunMapBranchesAsync(originalRunId, "map", new HashSet<int> { 0 }, teamId, userId, Guid.NewGuid());
        fork2.ShouldNotBe(fork1, "the branch reruns again once the reconciler released the orphaned lease");
    }

    [Fact]
    public async Task Rerun_side_effecting_map_branch_parks_on_approval_then_approve_fires_target_once_and_siblings_never_refire()
    {
        // CROWN JEWEL (D7-5 side-effecting body). Map over 4 elements; each branch body is a PURELY side-effecting
        // MutatingProbe keyed per element. Original: each fires once (counter[e_i]==1), map Success. Rerun branch 2:
        // the re-run target's side-effecting node parks on the D7-3 Approval gate (under map#2) — it does NOT
        // silently re-fire (counter[e2] still 1, run Suspended). On approve it fires EXACTLY ONCE (counter[e2]==2),
        // while the 3 seeded siblings are replayed terminal-only: ZERO node.started, counters unchanged at 1.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var probeKey = "mapbranch-se-approve-" + Guid.NewGuid().ToString("N");
        for (var i = 0; i < 4; i++) MutatingProbeNode.Reset($"{probeKey}-e{i}");

        var workflowId = await CreateWorkflowAsync(teamId, userId, SideEffectingBodyMapDef(probeKey));
        var originalRunId = await RunFreshAsync(workflowId, teamId, FourElements);
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Success);
        for (var i = 0; i < 4; i++) MutatingProbeNode.ExecutionsFor($"{probeKey}-e{i}").ShouldBe(1, $"branch e{i} fired once on the original");

        var rerunId = await RerunMapBranchAsync(originalRunId, "map", 2, teamId, userId);
        await RunEngineAsync(rerunId);

        // Parked on the gate — the side effect has NOT re-fired.
        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Suspended, "the re-run side-effecting branch parks on the approval gate");
        (await PendingApprovalWaitCountAsync(rerunId)).ShouldBe(1, "exactly one rerun-gate Approval wait is parked (for the single re-run branch)");
        MutatingProbeNode.ExecutionsFor($"{probeKey}-e2").ShouldBe(1, "the gate must NOT let the side effect fire before approval");

        // A legitimately SUSPENDED rerun KEEPS its lease — the rerun is genuinely still in flight (parked on the
        // human gate), so a concurrent re-rerun of the same branch must stay BLOCKED. This is the exact double-fire
        // the lease prevents: without it, a second rerun would fire the side effect again while the first is parked.
        (await ActiveLeaseCountAsync(originalRunId)).ShouldBe(1, "the parked fork still holds its branch-2 lease");
        await Should.ThrowAsync<RerunAlreadyInProgressException>(() =>
            RerunMapBranchViaCommandAsync(originalRunId, "map", 2, teamId, userId, Guid.NewGuid()));

        (await ApproveRerunGateAsync(rerunId, teamId, userId, approved: true)).ShouldBeTrue();
        await RunEngineAsync(rerunId);

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);
        (await ActiveLeaseCountAsync(originalRunId)).ShouldBe(0, "the lease frees once the parked fork resumes + completes");
        MutatingProbeNode.ExecutionsFor($"{probeKey}-e2").ShouldBe(2, "the approved target fired EXACTLY once more");
        MutatingProbeNode.ExecutionsFor($"{probeKey}-e0").ShouldBe(1, "sibling 0 was replayed — its side effect must NOT re-fire");
        MutatingProbeNode.ExecutionsFor($"{probeKey}-e1").ShouldBe(1, "sibling 1 replayed, no re-fire");
        MutatingProbeNode.ExecutionsFor($"{probeKey}-e3").ShouldBe(1, "sibling 3 replayed, no re-fire");
        (await BranchStartedCountAsync(rerunId, "se", "map#0")).ShouldBe(0, "a replayed sibling branch carries zero node.started on the fork");
        (await BranchStartedCountAsync(rerunId, "se", "map#1")).ShouldBe(0);
        (await BranchStartedCountAsync(rerunId, "se", "map#3")).ShouldBe(0);
    }

    [Fact]
    public async Task Rerun_side_effecting_map_branch_reject_skips_the_target_and_no_branch_refires()
    {
        // CROWN JEWEL — the fail-closed reject path. Rerun branch 1, then REJECT the gate: the side effect NEVER
        // fires (counter[e1] stays 1), the target branch settles Skipped, the run lands terminal (not stuck), and
        // no sibling re-fires. Anything but an explicit approve must not fire the effect.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var probeKey = "mapbranch-se-reject-" + Guid.NewGuid().ToString("N");
        for (var i = 0; i < 4; i++) MutatingProbeNode.Reset($"{probeKey}-e{i}");

        var workflowId = await CreateWorkflowAsync(teamId, userId, SideEffectingBodyMapDef(probeKey));
        var originalRunId = await RunFreshAsync(workflowId, teamId, FourElements);
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Success);

        var rerunId = await RerunMapBranchAsync(originalRunId, "map", 1, teamId, userId);
        await RunEngineAsync(rerunId);
        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Suspended);
        (await PendingApprovalWaitCountAsync(rerunId)).ShouldBe(1, "exactly one rerun-gate Approval wait is parked before the reject");

        (await ApproveRerunGateAsync(rerunId, teamId, userId, approved: false)).ShouldBeTrue();
        await RunEngineAsync(rerunId);

        for (var i = 0; i < 4; i++)
            MutatingProbeNode.ExecutionsFor($"{probeKey}-e{i}").ShouldBe(1, $"reject + replay must leave every counter at its original 1 — the effect never re-fired (e{i})");

        // The target branch's terminal was Skipped (the rejected node) → a Failed:false empty branch, so the map
        // still SUCCEEDS even under the default Terminate mode (a skipped branch is not a failed branch).
        (await BranchTerminalSkippedAsync(rerunId, "se", "map#1")).ShouldBeTrue("the rejected side-effecting node settled node.skipped");
        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success, "a rejected (skipped) side-effecting branch settles the fork to Success — NOT a Failure, NOT stuck Suspended");
        (await PendingApprovalWaitCountAsync(rerunId)).ShouldBe(0, "the gate wait resolved on reject — none stranded");
        (await BranchStartedCountAsync(rerunId, "se", "map#0")).ShouldBe(0, "siblings stay replayed under reject too");
        (await BranchStartedCountAsync(rerunId, "se", "map#2")).ShouldBe(0);
        (await BranchStartedCountAsync(rerunId, "se", "map#3")).ShouldBe(0);
    }

    [Fact]
    public async Task Rerun_branch_with_two_parallel_side_effecting_nodes_holds_both_until_both_approved_then_each_fires_once()
    {
        // The exactly-once-via-still-suspended regression (the doc-invariant finding): a single re-run branch
        // body whose parallel wave has TWO side-effecting nodes parks TWO gate waits at once. Neither effect
        // fires until BOTH are approved — the branch re-suspends as a whole while ANY gate is Pending, so an
        // approved node never advances past an un-approved sibling. Then each fires EXACTLY once.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var probeKey = "mapbranch-diamond-" + Guid.NewGuid().ToString("N");
        MutatingProbeNode.Reset($"{probeKey}-A");
        MutatingProbeNode.Reset($"{probeKey}-B");

        var workflowId = await CreateWorkflowAsync(teamId, userId, TwoSideEffectsDiamondMapDef(probeKey));
        var originalRunId = await RunFreshAsync(workflowId, teamId, """{ "things": ["x"] }""");
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Success);
        MutatingProbeNode.ExecutionsFor($"{probeKey}-A").ShouldBe(1, "seA fired once on the original");
        MutatingProbeNode.ExecutionsFor($"{probeKey}-B").ShouldBe(1, "seB fired once on the original");

        var rerunId = await RerunMapBranchAsync(originalRunId, "map", 0, teamId, userId);
        await RunEngineAsync(rerunId);

        // BOTH gates park at once under map#0; neither effect has re-fired.
        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Suspended);
        (await PendingApprovalWaitCountAsync(rerunId)).ShouldBe(2, "a parallel two-side-effect branch body parks TWO gate waits under map#0 simultaneously");
        MutatingProbeNode.ExecutionsFor($"{probeKey}-A").ShouldBe(1, "no effect fires before approval");
        MutatingProbeNode.ExecutionsFor($"{probeKey}-B").ShouldBe(1);

        // Approve the FIRST gate. The branch re-suspends (the other gate is still Pending) — the approved node
        // MUST NOT advance/fire while its sibling gate is unresolved.
        (await ApproveRerunGateAsync(rerunId, teamId, userId, approved: true)).ShouldBeTrue();
        await RunEngineAsync(rerunId);
        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Suspended, "still parked on the second gate");
        (await PendingApprovalWaitCountAsync(rerunId)).ShouldBe(1, "one gate resolved, one still pending");
        MutatingProbeNode.ExecutionsFor($"{probeKey}-A").ShouldBe(1, "the approved node MUST NOT fire while its sibling gate is still pending");
        MutatingProbeNode.ExecutionsFor($"{probeKey}-B").ShouldBe(1);

        // Approve the SECOND gate. Now the branch advances and BOTH nodes fire — each EXACTLY once.
        (await ApproveRerunGateAsync(rerunId, teamId, userId, approved: true)).ShouldBeTrue();
        await RunEngineAsync(rerunId);
        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);
        MutatingProbeNode.ExecutionsFor($"{probeKey}-A").ShouldBe(2, "seA fired exactly once more after both gates approved");
        MutatingProbeNode.ExecutionsFor($"{probeKey}-B").ShouldBe(2, "seB fired exactly once more after both gates approved");
    }

    [Fact]
    public async Task Rerun_reject_of_a_non_terminal_side_effecting_node_cascades_skip_to_the_distinct_branch_terminal()
    {
        // Reject a side-effecting node that is NOT the branch terminal: it settles Skipped, and the dead-edge
        // cascade skips the distinct downstream terminal too — the branch yields an empty result and the map
        // still SUCCEEDS (a skipped branch is Failed:false).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var probeKey = "mapbranch-cascade-" + Guid.NewGuid().ToString("N");
        for (var i = 0; i < 4; i++) MutatingProbeNode.Reset($"{probeKey}-e{i}");

        var workflowId = await CreateWorkflowAsync(teamId, userId, SideEffectingThenEmitMapDef(probeKey));
        var originalRunId = await RunFreshAsync(workflowId, teamId, FourElements);
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Success);

        var rerunId = await RerunMapBranchAsync(originalRunId, "map", 1, teamId, userId);
        await RunEngineAsync(rerunId);
        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Suspended);

        (await ApproveRerunGateAsync(rerunId, teamId, userId, approved: false)).ShouldBeTrue();
        await RunEngineAsync(rerunId);

        MutatingProbeNode.ExecutionsFor($"{probeKey}-e1").ShouldBe(1, "the rejected side-effecting node never re-fired");
        (await BranchTerminalSkippedAsync(rerunId, "se", "map#1")).ShouldBeTrue("the rejected non-terminal node settled Skipped");
        (await BranchTerminalSkippedAsync(rerunId, "emit", "map#1")).ShouldBeTrue("the distinct downstream terminal cascaded to Skipped via the dead edge");
        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success, "a fully-skipped branch is Failed:false → the map still succeeds");
    }

    [Fact]
    public async Task Rerun_map_branch_with_an_unopted_suspendable_body_node_is_refused()
    {
        // An un-opted suspendable body (SuspendProbe is CanSuspend WITHOUT IsRerunnableWhenSuspendable) stays
        // refused — only agent.code opts in. Pins the allowlist: lifting CanSuspend was NOT wholesale.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var probeKey = "mapbranch-suspbody-" + Guid.NewGuid().ToString("N");
        SuspendProbeNode.Reset(probeKey);
        var workflowId = await CreateWorkflowAsync(teamId, userId, SuspendableBodyMapDef(probeKey));
        // The original parks on the suspendable body — that's fine; the body-scan gate refuses regardless.
        var originalRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: FourElements);
        await RunEngineAsync(originalRunId);

        var before = await RunCountAsync(teamId);
        var ex = await Should.ThrowAsync<RerunBlockedByUnsupportedNodeException>(async () =>
            await RerunMapBranchAsync(originalRunId, "map", 0, teamId, userId));
        ex.BlockedNodeIds.ShouldContain("susp");

        (await RunCountAsync(teamId)).ShouldBe(before);
    }

    [Fact]
    public async Task Rerun_map_branch_with_a_both_side_effecting_and_suspendable_body_node_is_refused()
    {
        // The both-flag corruption guard (chat.post_message class): a node that is BOTH IsSideEffecting AND
        // CanSuspend is refused — the D7-3 gate would fire its effect on the approved walk then mis-skip it on the
        // node's own suspend-resume. BothFlagsProbe is the hermetic stand-in; the real-registry drift-detector
        // (below) proves chat.post_message itself is refused.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var probeKey = "mapbranch-both-" + Guid.NewGuid().ToString("N");
        var workflowId = await CreateWorkflowAsync(teamId, userId, BothFlagsBodyMapDef(probeKey));
        var originalRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: FourElements);
        await RunEngineAsync(originalRunId);

        var before = await RunCountAsync(teamId);
        var ex = await Should.ThrowAsync<RerunBlockedByUnsupportedNodeException>(async () =>
            await RerunMapBranchAsync(originalRunId, "map", 0, teamId, userId));
        ex.BlockedNodeIds.ShouldContain("both", "the both-flagged body node is named");

        (await RunCountAsync(teamId)).ShouldBe(before, "a both-flagged-body rerun must write nothing");
    }

    [Fact]
    public async Task RerunBranchBodyPolicy_over_the_real_registry_admits_agentcode_and_subworkflow_among_suspendable_nodes()
    {
        // DRIFT-DETECTOR over the REAL node registry: the fail-closed allowlist admits the re-stage opt-ins (agent.code
        // → fresh AgentRun; flow.subworkflow → fresh child run, D2) as suspendable branch bodies, refuses every OTHER
        // CanSuspend node, and refuses every both-flagged node. Fails the instant a new CanSuspend node author forgets
        // the opt-in, or a both-flagged node is admitted.
        using var scope = _fixture.BeginScope();
        var registry = scope.Resolve<INodeRegistry>();

        var admittedSuspendable = registry.All
            .Where(n => n.Manifest.CanSuspend && !RerunBranchBodyPolicy.IsRefusedAsBranchBody(n.Manifest))
            .Select(n => n.TypeKey)
            .OrderBy(k => k)
            .ToList();

        // agent.code + flow.subworkflow are the admitted suspendable branch bodies (the two ReStageExternalRun opt-ins);
        // a new CanSuspend node forgetting IsRerunnableWhenSuspendable (fail-closed default), or wrongly opting in, breaks this.
        admittedSuspendable.ShouldBe(new[] { "agent.code", "flow.subworkflow" });

        registry.All
            .Where(n => n.Manifest.IsSideEffecting && n.Manifest.CanSuspend)
            .ShouldAllBe(n => RerunBranchBodyPolicy.IsRefusedAsBranchBody(n.Manifest),
                "every BOTH side-effecting AND suspendable node (e.g. chat.post_message) is refused — the gate can't compose with the node's own suspend");

        // The ADMIT arm: every PURELY side-effecting node (git writes / http POST / issue ops / agent.run_command)
        // is admitted as a branch body — it routes through the D7-3 RerunSideEffectGate at runtime. Pin the set is
        // non-empty so a registry that lost all such nodes can't make this assertion vacuously true.
        var purelySideEffecting = registry.All.Where(n => n.Manifest.IsSideEffecting && !n.Manifest.CanSuspend).ToList();
        purelySideEffecting.ShouldNotBeEmpty("the production registry ships purely side-effecting nodes (git.open_pr, http.request, …)");
        purelySideEffecting.ShouldAllBe(n => !RerunBranchBodyPolicy.IsRefusedAsBranchBody(n.Manifest),
            "every purely side-effecting node is ADMITTED as a re-run branch body (gated at runtime by the D7-3 approval gate)");
    }

    [Fact]
    public void RerunDispositions_over_the_real_registry_match_the_from_node_gate_and_classify_the_re_stage_optins_distinctly()
    {
        // DRIFT-DETECTOR over the REAL registry for the FROM-NODE gate seam (the companion to the branch-policy one
        // above): every live node's disposition-based from-node admit equals the IsRerunUnsupported negation, AND the
        // re-stage opt-ins (agent.code + flow.subworkflow, D2) are the nodes earning ReStageExternalRun — the ones the
        // from-node arm admits — while every other suspendable node is fail-closed RefuseSuspendable.
        static bool PostP2_2Unsupported(NodeManifest m, string nodeId, string? exemptMapId) =>
            (m.CanSuspend && !(m.IsRerunnableWhenSuspendable && !m.IsSideEffecting))   // suspendable EXCEPT the agent.code re-stage opt-in
            || (m.Kind == NodeKind.Map && nodeId != exemptMapId) || m.Kind is NodeKind.Loop or NodeKind.Try;

        using var scope = _fixture.BeginScope();
        var registry = scope.Resolve<INodeRegistry>();

        // exempt cases: no exemption, this node IS the exempt target, and SOME OTHER map is exempt (the
        // `Map && nodeId != exemptMapId` non-exempt branch — exercised live, not just in the unit cube).
        foreach (var n in registry.All)
        foreach (var exemptMapId in new[] { (string?)null, n.TypeKey, "__some_other_map__" })
            RerunDispositions.Admits(n.Manifest, RerunContext.FromNodeRoot, n.TypeKey, exemptMapId)
                .ShouldBe(!PostP2_2Unsupported(n.Manifest, n.TypeKey, exemptMapId), $"from-node gate drifted for {n.TypeKey} exempt={exemptMapId ?? "null"}");

        var reStage = registry.All.Where(n => RerunDispositions.For(n.Manifest) == RerunDisposition.ReStageExternalRun).Select(n => n.TypeKey).OrderBy(k => k).ToList();
        // the re-stage opt-ins earning ReStageExternalRun — agent.code (fresh AgentRun) + flow.subworkflow (fresh child run, D2)
        reStage.ShouldBe(new[] { "agent.code", "flow.subworkflow" });

        // The headline behaviour over the LIVE registry: both re-stage opt-ins ARE admitted as a from-node root, every
        // OTHER suspendable node is NOT (fail-closed). The live counterpart of the unit cube's surgical-change proof.
        foreach (var typeKey in new[] { "agent.code", "flow.subworkflow" })
            RerunDispositions.Admits(registry.All.Single(n => n.TypeKey == typeKey).Manifest, RerunContext.FromNodeRoot, typeKey, exemptMapId: null)
                .ShouldBeTrue($"{typeKey} re-stages a fresh external run on the forked run id — admitted as a from-node root");

        registry.All.Where(n => n.Manifest.CanSuspend && n.TypeKey != "agent.code" && n.TypeKey != "flow.subworkflow").ToList()
            .ShouldAllBe(n => RerunDispositions.For(n.Manifest) == RerunDisposition.RefuseSuspendable
                && !RerunDispositions.Admits(n.Manifest, RerunContext.FromNodeRoot, n.TypeKey, null),
                "every other suspendable node stays fail-closed RefuseSuspendable + refused as a from-node root");

        registry.All.Select(n => RerunDispositions.For(n.Manifest)).Distinct().Count().ShouldBeGreaterThan(1, "the live registry spans multiple dispositions (non-vacuous)");
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

    // terminate-mode map (default), per-element flaky[failTimes] → echo terminal → synth. failTimes>0 ⇒ the original
    // run terminate-FAILS (every branch fails its first attempts); a rerun re-runs the chosen branches at a higher
    // attempt — failTimes=1 ⇒ it passes (counter→2), failTimes=99 ⇒ it keeps failing. Mirrors CountingMapDef.
    private static WorkflowDefinition TerminateFailingMapDef(string counterPrefix, int failTimes) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "map", TypeKey = "flow.map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "items": "{{trigger.things}}" }""") },
            new() { Id = "ms", TypeKey = "flow.map_start", ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "flaky", TypeKey = FlakyTestNode.Key, ParentId = "map",
                    Config = WorkflowsTestSeed.Json($$"""{ "key": "{{counterPrefix}}-{{"{{"}}item{{"}}"}}", "failTimes": {{failTimes}} }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
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

    // terminate-mode map whose per-element failure is CONTROLLED by each element ({v, ft}): flaky key={prefix}-{{item.v}},
    // failTimes={{item.ft}} — so one element can fail (ft:1) while siblings pass (ft:0), giving a partially-failed map.
    private static WorkflowDefinition ElementControlledTerminateMapDef(string counterPrefix) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "map", TypeKey = "flow.map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "items": "{{trigger.things}}" }""") },
            new() { Id = "ms", TypeKey = "flow.map_start", ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "flaky", TypeKey = FlakyTestNode.Key, ParentId = "map",
                    Config = WorkflowsTestSeed.Json($$"""{ "key": "{{counterPrefix}}-{{"{{"}}item.v{{"}}"}}", "failTimes": "{{"{{"}}item.ft{{"}}"}}" }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "echo", TypeKey = JsonEmitNode.Key, ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "item": "{{item.v}}", "attempts": "{{nodes.flaky.outputs.attempts}}" }""") },
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

    // terminate-mode map whose body node FAILS but is HANDLED by its OWN in-body error edge: flaky[failTimes=99]
    // always fails → routes down its error handle to a recover node that succeeds → the branch SUCCEEDS, so a
    // handled failure is NOT a terminate point and the map SUCCEEDS (never Failure). Exercises in-body error routing
    // inside a map branch + the rerun reuse of an error-edge-recovered sibling — the inverse of the terminate arm's
    // !HasErrorEdgeInDefinition guard (the same predicate the continue arm uses to skip handled failures).
    private static WorkflowDefinition EdgeHandledTerminateMapDef(string counterPrefix) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "map", TypeKey = "flow.map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "items": "{{trigger.things}}" }""") },
            new() { Id = "ms", TypeKey = "flow.map_start", ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "flaky", TypeKey = FlakyTestNode.Key, ParentId = "map",
                    Config = WorkflowsTestSeed.Json($$"""{ "key": "{{counterPrefix}}-{{"{{"}}item{{"}}"}}", "failTimes": 99 }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "recover", TypeKey = JsonEmitNode.Key, ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(),
                    Inputs = WorkflowsTestSeed.Json("""{ "item": "{{item}}", "recovered": true }""") },
            new() { Id = "synth", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "agg": "{{nodes.map.outputs.results}}" }""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "map" },
            new() { From = "map", To = "synth" },
            new() { From = "synth", To = "end" },
            new() { From = "ms", To = "flaky" },
            new() { From = "flaky", To = "recover", SourceHandle = WorkflowHandles.Error },
        },
    };

    // items binds to a SCALAR trigger field → resolves to a non-array → the map fails BEFORE fan-out (zero branch rows).
    private static WorkflowDefinition NonArrayItemsMapDef() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "map", TypeKey = "flow.map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "items": "{{trigger.scalar}}" }""") },
            new() { Id = "ms", TypeKey = "flow.map_start", ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "flaky", TypeKey = FlakyTestNode.Key, ParentId = "map",
                    Config = WorkflowsTestSeed.Json("""{ "key": "x", "failTimes": 0 }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
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

    // map body = a parallel diamond with TWO side-effecting nodes (seA, seB), per-element-keyed, merging into a
    // single JsonEmitNode terminal "merge". Both are admitted (purely side-effecting); on a rerun both park gates.
    private static WorkflowDefinition TwoSideEffectsDiamondMapDef(string probeKey) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "map", TypeKey = "flow.map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "items": "{{trigger.things}}" }""") },
            new() { Id = "ms", TypeKey = "flow.map_start", ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "seA", TypeKey = MutatingProbeNode.Key, ParentId = "map", Config = WorkflowsTestSeed.Json($$"""{ "key": "{{probeKey}}-A" }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "seB", TypeKey = MutatingProbeNode.Key, ParentId = "map", Config = WorkflowsTestSeed.Json($$"""{ "key": "{{probeKey}}-B" }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "merge", TypeKey = JsonEmitNode.Key, ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "item": "{{item}}" }""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "map" },
            new() { From = "map", To = "end" },
            new() { From = "ms", To = "seA" },
            new() { From = "ms", To = "seB" },
            new() { From = "seA", To = "merge" },
            new() { From = "seB", To = "merge" },
        },
    };

    // map body = side-effecting "se" → pure JsonEmitNode terminal "emit". A reject of "se" skips it AND cascades
    // the skip to the distinct downstream terminal "emit".
    private static WorkflowDefinition SideEffectingThenEmitMapDef(string probeKey) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "map", TypeKey = "flow.map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "items": "{{trigger.things}}" }""") },
            new() { Id = "ms", TypeKey = "flow.map_start", ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "se", TypeKey = MutatingProbeNode.Key, ParentId = "map", Config = WorkflowsTestSeed.Json($$"""{ "key": "{{probeKey}}-{{"{{"}}item{{"}}"}}" }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "emit", TypeKey = JsonEmitNode.Key, ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "item": "{{item}}" }""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "map" },
            new() { From = "map", To = "end" },
            new() { From = "ms", To = "se" },
            new() { From = "se", To = "emit" },
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

    // map body contains a BOTH side-effecting AND suspendable node ("both" = BothFlagsProbe) → the both-flag arm refuses.
    private static WorkflowDefinition BothFlagsBodyMapDef(string probeKey) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "map", TypeKey = "flow.map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "items": "{{trigger.things}}" }""") },
            new() { Id = "ms", TypeKey = "flow.map_start", ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "both", TypeKey = BothFlagsProbeNode.Key, ParentId = "map", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "map" },
            new() { From = "map", To = "end" },
            new() { From = "ms", To = "both" },
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

    // Stage + walk a SNAPSHOT-origin run (WorkflowId null, inline frozen definition) via the real snapshot starter —
    // so a rerun of it forks through RunFromSnapshotStarter (not the authored RunStarter), exercising that path.
    private async Task<Guid> RunSnapshotFreshAsync(WorkflowDefinition def, Guid teamId, Guid userId, string payloadJson)
    {
        Guid runId;
        using (var scope = _fixture.BeginScope())
            runId = await scope.Resolve<IRunFromSnapshotStarter>().StartFromSnapshotAsync(def, teamId, userId, payloadJson, scopeRepositoryIds: null, projectionKind: null, session: null, CancellationToken.None);

        await RunEngineAsync(runId);
        return runId;
    }

    private async Task<Guid> RerunMapBranchAsync(Guid originalRunId, string mapNodeId, int branchIndex, Guid teamId, Guid userId, Guid? operationId = null)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IWorkflowService>().RerunMapBranchAsync(originalRunId, mapNodeId, branchIndex, teamId, userId, operationId, CancellationToken.None);
    }

    private async Task<Guid> RerunMapBranchesAsync(Guid originalRunId, string mapNodeId, IReadOnlySet<int> branchIndices, Guid teamId, Guid userId, Guid? operationId = null)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IWorkflowService>().RerunMapBranchesAsync(originalRunId, mapNodeId, branchIndices, teamId, userId, operationId, CancellationToken.None);
    }

    // Drives the rerun through the REAL mediator pipeline (TransactionalBehavior + the team/user context), so the
    // idempotency dedup runs under the same one-transaction-per-command boundary production uses.
    private async Task<Guid> RerunMapBranchViaCommandAsync(Guid originalRunId, string mapNodeId, int branchIndex, Guid teamId, Guid userId, Guid? operationId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new RerunMapBranchCommand { OriginalRunId = originalRunId, MapNodeId = mapNodeId, BranchIndex = branchIndex, OperationId = operationId });
    }

    private async Task<Guid> RerunMapBranchesViaCommandAsync(Guid originalRunId, string mapNodeId, IReadOnlyList<int> branchIndices, Guid teamId, Guid userId, Guid? operationId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new RerunMapBranchesCommand { OriginalRunId = originalRunId, MapNodeId = mapNodeId, BranchIndices = branchIndices, OperationId = operationId });
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

    private async Task<int> ActiveLeaseCountAsync(Guid originalRunId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRerunLease.AsNoTracking()
            .CountAsync(l => l.OriginalRunId == originalRunId && l.Status == RerunLeaseStatuses.InProgress);
    }

    private async Task CancelRunAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowService>().CancelRunAsync(runId, teamId, CancellationToken.None);
    }

    private async Task<StuckRunReconcileSummary> ReconcileAsync()
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IStuckRunReconcilerService>().ReconcileAsync(CancellationToken.None);
    }

    /// <summary>Resolve a pending rerun-gate Approval wait through the REAL ResumeRunCommand chain (→ ApproveRunAsync), as an operator would.</summary>
    private async Task<bool> ApproveRerunGateAsync(Guid runId, Guid teamId, Guid userId, bool approved)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new ResumeRunCommand { RunId = runId, Approved = approved, Comment = approved ? "go" : "skip" });
    }

    private async Task<int> PendingApprovalWaitCountAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking()
            .CountAsync(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending && w.WaitKind == WorkflowWaitKinds.Approval);
    }

    private async Task<bool> BranchTerminalSkippedAsync(Guid runId, string nodeId, string branchKey)
    {
        using var scope = _fixture.BeginScope();
        var status = await scope.Resolve<CodeSpaceDbContext>().WorkflowRunNode.AsNoTracking()
            .Where(n => n.RunId == runId && n.NodeId == nodeId && n.IterationKey == branchKey)
            .Select(n => (NodeStatus?)n.Status)
            .SingleOrDefaultAsync();
        return status == NodeStatus.Skipped;
    }

}
