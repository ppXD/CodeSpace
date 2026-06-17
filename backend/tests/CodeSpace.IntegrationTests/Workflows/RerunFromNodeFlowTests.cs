using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Variables;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Engine;
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
/// From-node rerun (D7) — drives the REAL <see cref="IWorkflowService.RerunFromNodeAsync"/> seam against a real
/// engine + real Postgres, then re-walks the fork with the real <see cref="IWorkflowEngine"/> (no hand-staged
/// rows — unlike the replay tests' <c>StageReplayAsync</c>, this exercises the production planner → side-effect
/// gate → reusability resolver → forked staging → cell pre-seed → RehydrateFromLedger resume chain end-to-end).
///
/// <para>Tier: high-fidelity — real <see cref="RerunFromNodeAsync"/>, real <see cref="IRunFromSnapshotStarter"/>
/// (snapshot origin), real <see cref="IVariableService"/> (frozen-scope), real engine re-walk. The crown jewel is
/// the <see cref="MutatingProbeNode"/> execution counter: a node that is REUSED never re-enters RunAsync, so its
/// counter is unchanged — hard proof the upstream output was carried forward, not recomputed. The generic
/// reuse-vs-rerun discriminator across every shape is the <c>node.started</c> ledger count: 0 for a reused cell
/// (pre-seeded terminal-only), 1 for a genuinely re-run node.</para>
///
/// <para>Coverage: happy-path reuse (linear / diamond / skipped-branch / failed-with-error-edge / completed
/// container / root≡replay); origin variants + frozen scope (authored / snapshot / empty-snapshot sentinel /
/// frozen plain var); lineage + atomic pre-seed + original-immutability; and every fail-closed gate (cross-team
/// 404, unknown node, side-effecting node in closure, non-reusable kept upstream) — each asserting NOTHING was
/// written.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class RerunFromNodeFlowTests
{
    private readonly PostgresFixture _fixture;

    public RerunFromNodeFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    // ─────────────────────────────────────────────────────────────────────────────
    //  A. Happy-path reuse — the crown jewels
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rerun_from_midpoint_reuses_side_effecting_upstream_without_re_executing_it()
    {
        // CROWN JEWEL. start → mutator(side-effecting, counts executions) → transform(echoes mutator.n) → end.
        // Rerun from "transform": the mutator is UPSTREAM (kept + reused), the transform + end re-run. If reuse
        // is real, the mutator's RunAsync never fires again (counter stays 1) and the transform re-computes from
        // the CARRIED-FORWARD mutator output (n=1). A broken reuse would re-run the mutator → counter 2, n=2.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var probeKey = "rerun-crown-" + Guid.NewGuid().ToString("N");
        MutatingProbeNode.Reset(probeKey);

        var workflowId = await CreateWorkflowAsync(teamId, userId, MutatorTransformDef(probeKey));
        var originalRunId = await RunFreshAsync(workflowId, teamId);

        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Success);
        MutatingProbeNode.ExecutionsFor(probeKey).ShouldBe(1, "the original run executes the mutator exactly once");

        var rerunId = await RerunAsync(originalRunId, "transform", teamId, userId);
        await RunEngineAsync(rerunId);

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);
        MutatingProbeNode.ExecutionsFor(probeKey).ShouldBe(1,
            "the mutator is a REUSED upstream node — it must NOT re-execute on the rerun (counter would be 2 if it did)");

        // node.started discriminator: reused cells carry zero starts; re-run cells carry exactly one (no retries).
        (await NodeStartedCountAsync(rerunId, "start")).ShouldBe(0, "reused trigger node was not re-run");
        (await NodeStartedCountAsync(rerunId, "mutator")).ShouldBe(0, "reused side-effecting node was not re-run");
        (await NodeStartedCountAsync(rerunId, "transform")).ShouldBe(1, "the from-node re-ran exactly once");
        (await NodeStartedCountAsync(rerunId, "end")).ShouldBe(1, "the downstream node re-ran exactly once");

        // The transform recomputed from the carried-forward mutator output → n is still 1, proving the reused
        // upstream OUTPUT (not just the absence of execution) flowed into the re-run frontier.
        var transformCell = await LoadCellAsync(rerunId, "transform");
        JsonDocument.Parse(transformCell.OutputsJson).RootElement.GetProperty("n").GetInt32()
            .ShouldBe(1, "the re-run transform must echo the REUSED mutator output, not a recomputed one");

        // Exactly-once-on-fork: after the walk, every cell — reused or re-run — carries exactly ONE terminal
        // record. A reused cell that the engine wrongly re-settled, or a re-run node that double-fired, would
        // show two. This pins the durable ledger's exactly-once property across the rerun re-walk.
        foreach (var n in new[] { "start", "mutator", "transform", "end" })
            (await TerminalRecordCountAsync(rerunId, n)).ShouldBe(1, $"node '{n}' must have exactly one terminal record on the fork");
    }

    [Fact]
    public async Task Rerun_from_diamond_join_reuses_every_upstream_arm()
    {
        // start → A → {B, C} → D(join) → end. Rerun from the join D reuses start, A, B, C; re-runs D + end. The
        // node.started counts prove the whole fan-out/fan-in upstream subgraph was carried forward as one frontier.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, DiamondDef());
        var originalRunId = await RunFreshAsync(workflowId, teamId);
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Success);

        var rerunId = await RerunAsync(originalRunId, "d", teamId, userId);
        await RunEngineAsync(rerunId);

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);
        foreach (var kept in new[] { "start", "a", "b", "c" })
            (await NodeStartedCountAsync(rerunId, kept)).ShouldBe(0, $"diamond upstream '{kept}' was reused, not re-run");
        (await NodeStartedCountAsync(rerunId, "d")).ShouldBe(1, "the join re-ran exactly once");
        (await NodeStartedCountAsync(rerunId, "end")).ShouldBe(1, "the terminal re-ran exactly once");
    }

    [Fact]
    public async Task Rerun_reuses_skipped_branch_as_skipped()
    {
        // start → if(true) → {T runs, F skipped} → J(join) → end. Rerun from J reuses the branch: T as Success,
        // F as Skipped (the NodeSkipped seeder path), the if with its routing hints. The fork must faithfully
        // carry F's skipped state — not re-enliven the dead branch.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, BranchJoinDef());
        var originalRunId = await RunFreshAsync(workflowId, teamId, payloadJson: """{"branch":"yes"}""");
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Success);
        (await LoadCellAsync(originalRunId, "f")).Status.ShouldBe(NodeStatus.Skipped, "the false branch was skipped originally");

        var rerunId = await RerunAsync(originalRunId, "j", teamId, userId);
        await RunEngineAsync(rerunId);

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);
        (await LoadCellAsync(rerunId, "f")).Status.ShouldBe(NodeStatus.Skipped,
            "a reused skipped branch must STAY skipped on the fork (NodeSkipped pre-seed path)");
        (await NodeStartedCountAsync(rerunId, "f")).ShouldBe(0, "the reused skipped branch did not re-run");
        (await NodeStartedCountAsync(rerunId, "t")).ShouldBe(0, "the reused taken branch did not re-run");
        (await NodeStartedCountAsync(rerunId, "j")).ShouldBe(1, "the join re-ran exactly once");

        // Load-bearing routing-hints carry-forward: the kept branch cell's frozen hints MUST survive onto the
        // fork. A seeder that dropped them would re-emit the iff cell with null hints — and null hints make
        // IsEdgeLive treat BOTH handles as live, so the originally-dead 'false' edge would re-enliven. Assert the
        // exact frozen hint is carried so this test fails if hints are ever dropped.
        var iffCell = await LoadCellAsync(rerunId, "iff");
        iffCell.RoutingHintsJson.ShouldNotBeNull("the reused branch cell must carry its frozen routing hints, not null");
        JsonDocument.Parse(iffCell.RoutingHintsJson!).RootElement.EnumerateArray().Select(e => e.GetString())
            .ShouldBe(new[] { "true" }, "the carried-forward hint must be the original frozen decision ('true'), so the dead 'false' edge stays dead");
    }

    [Fact]
    public async Task Rerun_reuses_failed_upstream_and_a_re_run_node_consumes_the_rebuilt_error_output()
    {
        // start → flaky(always fails) =(error)=> caught(reads {{flaky.error.message}}) → end ; flaky =(normal)=> ok.
        // The original routes the error branch. Rerun from "caught": flaky is REUSED as a failed source (the
        // NodeFailed seeder path), and caught RE-RUNS — so this proves the full rehydrate→BuildErrorOutput→
        // downstream-consume path: a re-run node reads the error output rebuilt from the reused failed cell.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var flakyKey = "rerun-failed-" + Guid.NewGuid().ToString("N");
        var workflowId = await CreateWorkflowAsync(teamId, userId, FailWithErrorEdgeDef(flakyKey));
        var originalRunId = await RunFreshAsync(workflowId, teamId);
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Success);
        (await LoadCellAsync(originalRunId, "flaky")).Status.ShouldBe(NodeStatus.Failure);
        var attemptsAfterOriginal = FlakyTestNode.AttemptsFor(flakyKey);

        var rerunId = await RerunAsync(originalRunId, "caught", teamId, userId);
        await RunEngineAsync(rerunId);

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);
        (await LoadCellAsync(rerunId, "flaky")).Status.ShouldBe(NodeStatus.Failure,
            "the reused failed-with-error-edge upstream must be re-settled as a failed source");
        (await NodeStartedCountAsync(rerunId, "flaky")).ShouldBe(0, "the reused failed node did not re-run");
        FlakyTestNode.AttemptsFor(flakyKey).ShouldBe(attemptsAfterOriginal,
            "reuse must not re-invoke the failing node (attempt count unchanged from the original run)");
        (await NodeStartedCountAsync(rerunId, "caught")).ShouldBe(1, "the from-node re-ran exactly once");

        // The crux: the RE-RUN caught node resolved {{nodes.flaky.outputs.error.message}} from the error output
        // the engine REBUILT from the reused (failed) flaky cell — not from a stale copy.
        var caughtCell = await LoadCellAsync(rerunId, "caught");
        JsonDocument.Parse(caughtCell.OutputsJson).RootElement.GetProperty("msg").GetString()
            .ShouldContain("flaky failure", customMessage: "the re-run node must consume the rebuilt error output of the reused failed upstream");
    }

    [Fact]
    public async Task Rerun_reuses_completed_container_as_a_single_leaf_body_not_re_entered()
    {
        // start → loop(1 pass; body: loop_start → mutator) → transform → end. Rerun from "transform" keeps the
        // loop as ONE settled top-level leaf — its body (the side-effecting mutator inside) is never re-entered.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var bodyKey = "rerun-loopbody-" + Guid.NewGuid().ToString("N");
        MutatingProbeNode.Reset(bodyKey);

        var workflowId = await CreateWorkflowAsync(teamId, userId, LoopBodyDef(bodyKey));
        var originalRunId = await RunFreshAsync(workflowId, teamId);
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Success);
        MutatingProbeNode.ExecutionsFor(bodyKey).ShouldBe(1, "the loop body executed once originally");

        var rerunId = await RerunAsync(originalRunId, "transform", teamId, userId);
        await RunEngineAsync(rerunId);

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);
        MutatingProbeNode.ExecutionsFor(bodyKey).ShouldBe(1,
            "a reused container is one settled leaf — the engine must NOT re-enter its body on the rerun");
        (await NodeStartedCountAsync(rerunId, "loop")).ShouldBe(0, "the reused container was not re-run at top level");
        (await NodeStartedCountAsync(rerunId, "transform")).ShouldBe(1, "the from-node re-ran exactly once");
    }

    [Fact]
    public async Task Rerun_from_the_root_node_re_runs_everything_like_a_replay()
    {
        // Rerun from the trigger (root): the closure is the WHOLE graph, the kept set is empty, nothing is
        // pre-seeded — the degenerate case that is equivalent to a frozen replay. Every node re-runs once.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, LinearEchoDef());
        var originalRunId = await RunFreshAsync(workflowId, teamId);
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Success);

        var rerunId = await RerunAsync(originalRunId, "start", teamId, userId);
        await RunEngineAsync(rerunId);

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);
        foreach (var n in new[] { "start", "a", "end" })
            (await NodeStartedCountAsync(rerunId, n)).ShouldBe(1, $"rerun-from-root re-runs every node, including '{n}'");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  B. Origin variants + frozen scope
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rerun_from_a_snapshot_origin_run_forks_the_inline_definition()
    {
        // A snapshot run (no Workflow row — inline frozen definition). Rerun must plan over the INLINE definition,
        // fork version-less, and carry the same frozen definition JSON.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var originalRunId = await StartSnapshotAsync(teamId, userId, LinearEchoDef());
        await RunEngineAsync(originalRunId);
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Success);

        var rerunId = await RerunAsync(originalRunId, "a", teamId, userId);
        await RunEngineAsync(rerunId);

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var original = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == originalRunId);
        var rerun = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == rerunId);
        var rerunRequest = await db.WorkflowRunRequest.AsNoTracking().SingleAsync(r => r.Id == rerun.RunRequestId);

        rerun.WorkflowId.ShouldBeNull("a snapshot-origin rerun stays version-less (no Workflow row)");
        rerun.DefinitionSnapshotJson.ShouldBe(original.DefinitionSnapshotJson, "the fork carries the original's frozen inline definition");
        rerunRequest.SourceType.ShouldBe(WorkflowRunSourceTypes.Rerun,
            "the snapshot-fork must carry source=rerun (the sourceType threaded into StageReplayFromSnapshotAsync)");
        (await NodeStartedCountAsync(rerunId, "start")).ShouldBe(0, "the snapshot-origin upstream was reused");
        (await NodeStartedCountAsync(rerunId, "a")).ShouldBe(1, "the from-node re-ran exactly once");
    }

    [Fact]
    public async Task Rerun_with_an_empty_original_snapshot_writes_the_replay_sentinel()
    {
        // The definitions here declare no wf/team variables, so the original snapshot is empty. The seeder must
        // write a Sys-scoped "__rerun__" sentinel variable so the engine takes the REPLAY (frozen) scope fork
        // even though there are no plain values to freeze — without it the engine would take the fresh path.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, LinearEchoDef());
        var originalRunId = await RunFreshAsync(workflowId, teamId);
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Success);

        var rerunId = await RerunAsync(originalRunId, "a", teamId, userId);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var sentinel = await db.WorkflowRunVariable.AsNoTracking()
                .SingleOrDefaultAsync(v => v.RunId == rerunId && v.Scope == "Sys" && v.Name == "__rerun__");
            sentinel.ShouldNotBeNull("an empty original snapshot must seed the replay-path sentinel row");
        }

        await RunEngineAsync(rerunId);
        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);
    }

    [Fact]
    public async Task Rerun_reads_a_re_run_nodes_plain_variable_from_the_frozen_snapshot_not_live()
    {
        // start → a(echoes {{team.REGION}}) → b → end. The original snapshots REGION. We then MUTATE the live
        // team variable and rerun from "a" (so "a" RE-RUNS and reads REGION again). The fork must read the FROZEN
        // snapshot value, not the mutated live one — the replay-scope contract for a re-run node.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await SetTeamVarAsync(teamId, userId, "REGION", "us-east-1");

        var workflowId = await CreateWorkflowAsync(teamId, userId, EchoTeamVarDef("REGION"));
        var originalRunId = await RunFreshAsync(workflowId, teamId);
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Success);

        await SetTeamVarAsync(teamId, userId, "REGION", "eu-west-2");   // rotate AFTER the original

        var rerunId = await RerunAsync(originalRunId, "a", teamId, userId);
        await RunEngineAsync(rerunId);

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);
        var aCell = await LoadCellAsync(rerunId, "a");
        JsonDocument.Parse(aCell.OutputsJson).RootElement.GetProperty("v").GetString()
            .ShouldBe("us-east-1", "a re-run node must read the FROZEN snapshot value, not the mutated live variable");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  C. Lineage, atomic pre-seed, original immutability
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rerun_stamps_parent_causation_and_rerun_source_type()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, LinearEchoDef());
        var originalRunId = await RunFreshAsync(workflowId, teamId);

        Guid originalRequestId;
        using (var scope = _fixture.BeginScope())
            originalRequestId = await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking()
                .Where(r => r.Id == originalRunId).Select(r => r.RunRequestId).SingleAsync();

        var rerunId = await RerunAsync(originalRunId, "a", teamId, userId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var rerun = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == rerunId);
        var request = await db.WorkflowRunRequest.AsNoTracking().SingleAsync(r => r.Id == rerun.RunRequestId);

        rerun.ParentRunId.ShouldBe(originalRunId, "rerun lineage: fork parents the original run");
        request.SourceType.ShouldBe(WorkflowRunSourceTypes.Rerun, "the fork request is tagged source=rerun");
        request.CausationId.ShouldBe(originalRequestId, "rerun lineage: the request links back to the original's request");
    }

    [Fact]
    public async Task Rerun_pre_seeds_kept_cells_as_terminal_only_records_before_the_engine_runs()
    {
        // Atomicity + shape of the pre-seed: BEFORE the fork is walked, the kept cells already project through the
        // workflow_run_node view as settled (Success) with ZERO node.started records (terminal-only re-emit), and
        // the closure nodes have no cell yet. The whole stage+seed lands in one transaction (the run is dispatchable).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, LinearEchoDef());
        var originalRunId = await RunFreshAsync(workflowId, teamId);

        var rerunId = await RerunAsync(originalRunId, "a", teamId, userId);   // staged + dispatched, NOT yet walked

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var startCell = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == rerunId && n.NodeId == "start" && n.IterationKey == "");
        startCell.Status.ShouldBe(NodeStatus.Success, "the kept upstream cell is pre-seeded as settled before the walk");
        (await NodeStartedCountAsync(rerunId, "start")).ShouldBe(0, "the pre-seed is terminal-only — no node.started record");

        var closureCellExists = await db.WorkflowRunNode.AsNoTracking().AnyAsync(n => n.RunId == rerunId && n.NodeId == "a" && n.IterationKey == "");
        closureCellExists.ShouldBeFalse("the from-node is NOT pre-seeded — it re-runs from scratch");

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == rerunId);
        run.Status.ShouldBeOneOf(WorkflowRunStatus.Enqueued, WorkflowRunStatus.Pending);
    }

    [Fact]
    public async Task Rerun_never_mutates_the_original_run()
    {
        // The fork is pure-additive: the original run's cells, its records, and a side-effecting node's counter
        // must all be byte-identical after a rerun (the seeder INSERTs onto the fork, never touches the parent).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var probeKey = "rerun-immut-" + Guid.NewGuid().ToString("N");
        MutatingProbeNode.Reset(probeKey);

        var workflowId = await CreateWorkflowAsync(teamId, userId, MutatorTransformDef(probeKey));
        var originalRunId = await RunFreshAsync(workflowId, teamId);

        int originalRecordCount = await RecordCountAsync(originalRunId);
        int originalCellCount = await CellCountAsync(originalRunId);

        var rerunId = await RerunAsync(originalRunId, "transform", teamId, userId);
        await RunEngineAsync(rerunId);
        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);

        (await RecordCountAsync(originalRunId)).ShouldBe(originalRecordCount, "the original run's ledger is untouched by a rerun");
        (await CellCountAsync(originalRunId)).ShouldBe(originalCellCount, "the original run's node cells are untouched by a rerun");
        MutatingProbeNode.ExecutionsFor(probeKey).ShouldBe(1, "the original's side effect is not re-fired by the fork");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  D. Fail-closed gates — each asserts NOTHING was written
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rerun_for_a_run_in_another_team_throws_not_found_and_writes_nothing()
    {
        var (teamA, userA) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (teamB, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamA, userA, LinearEchoDef());
        var originalRunId = await RunFreshAsync(workflowId, teamA);

        var before = await RunCountAsync(teamB);
        await Should.ThrowAsync<KeyNotFoundException>(async () =>
            await RerunAsync(originalRunId, "a", teamB, userA));   // teamB does not own the run → 404-conflated

        (await RunCountAsync(teamB)).ShouldBe(before, "a cross-team rerun must write nothing");
    }

    [Fact]
    public async Task Rerun_from_an_unknown_node_throws_target_not_found_and_writes_nothing()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, LinearEchoDef());
        var originalRunId = await RunFreshAsync(workflowId, teamId);

        var before = await RunCountAsync(teamId);
        await Should.ThrowAsync<RerunTargetNotFoundException>(async () =>
            await RerunAsync(originalRunId, "does-not-exist", teamId, userId));

        (await RunCountAsync(teamId)).ShouldBe(before, "an unknown from-node must write nothing");
    }

    [Fact]
    public async Task Rerun_with_a_side_effecting_node_in_the_closure_is_refused_and_writes_nothing()
    {
        // Rerun FROM the side-effecting node itself → it is inside the re-run closure → the gate refuses (slice-1
        // fail-closed: re-running it would re-fire the side effect / re-bill). The blocked id is surfaced.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var probeKey = "rerun-gate-" + Guid.NewGuid().ToString("N");
        MutatingProbeNode.Reset(probeKey);

        var workflowId = await CreateWorkflowAsync(teamId, userId, MutatorTransformDef(probeKey));
        var originalRunId = await RunFreshAsync(workflowId, teamId);
        var before = await RunCountAsync(teamId);

        var ex = await Should.ThrowAsync<RerunBlockedBySideEffectException>(async () =>
            await RerunAsync(originalRunId, "mutator", teamId, userId));
        ex.BlockedNodeIds.ShouldContain("mutator", "the refusal must name the effectful node in the closure");

        (await RunCountAsync(teamId)).ShouldBe(before, "a side-effect-blocked rerun must write nothing");
        MutatingProbeNode.ExecutionsFor(probeKey).ShouldBe(1, "the side effect is not re-fired by a refused rerun");
    }

    [Fact]
    public async Task Rerun_when_a_kept_upstream_did_not_settle_reusably_is_refused_and_writes_nothing()
    {
        // start → flaky(always fails, NO error edge) → end. The original FAILS at flaky; end never runs. Rerun
        // from "end" keeps flaky, which failed WITHOUT an error edge → no reusable outcome → hard refuse.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var flakyKey = "rerun-nonreuse-" + Guid.NewGuid().ToString("N");
        var workflowId = await CreateWorkflowAsync(teamId, userId, FailNoErrorEdgeDef(flakyKey));
        var originalRunId = await RunFreshAsync(workflowId, teamId);
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Failure);

        var before = await RunCountAsync(teamId);
        await Should.ThrowAsync<RerunUpstreamNotReusableException>(async () =>
            await RerunAsync(originalRunId, "end", teamId, userId));

        (await RunCountAsync(teamId)).ShouldBe(before, "a non-reusable-upstream rerun must write nothing");
    }

    [Fact]
    public async Task Rerun_when_a_kept_upstream_is_still_suspended_is_refused_and_writes_nothing()
    {
        // start → suspendprobe(parks an Action wait) → downstream → end. The original SUSPENDS at suspendprobe;
        // downstream never runs. Rerun from "downstream" keeps suspendprobe, whose cell is non-terminal
        // (Suspended) → no reusable outcome → hard refuse (the in-flight-upstream guard).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var probeKey = "rerun-suspended-" + Guid.NewGuid().ToString("N");
        SuspendProbeNode.Reset(probeKey);

        var workflowId = await CreateWorkflowAsync(teamId, userId, SuspendThenDownstreamDef(probeKey));
        var originalRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await RunEngineAsync(originalRunId);
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Suspended);

        var before = await RunCountAsync(teamId);
        await Should.ThrowAsync<RerunUpstreamNotReusableException>(async () =>
            await RerunAsync(originalRunId, "downstream", teamId, userId));

        (await RunCountAsync(teamId)).ShouldBe(before, "a rerun over an in-flight (suspended) kept upstream must write nothing");
    }

    [Fact]
    public async Task Rerun_with_a_container_in_the_closure_is_refused_and_writes_nothing()
    {
        // The slice-1 conservative gate: a Map/Loop/Try container in the re-run closure is refused by KIND (re-
        // running a container re-runs its whole body atomically; approval-gated container rerun is the D7-3/4
        // follow-up). Here the loop body is PURE (json_emit) yet the loop is still refused — this PINS that
        // intended conservatism so a future body-effectfulness relaxation is a deliberate, test-visible change.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, PureBodiedLoopDef());
        var originalRunId = await RunFreshAsync(workflowId, teamId);
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Success);

        var before = await RunCountAsync(teamId);
        var ex = await Should.ThrowAsync<RerunBlockedBySideEffectException>(async () =>
            await RerunAsync(originalRunId, "start", teamId, userId));   // closure includes the loop container
        ex.BlockedNodeIds.ShouldContain("loop", "a container in the closure is refused by Kind (slice-1 conservatism)");

        (await RunCountAsync(teamId)).ShouldBe(before, "a container-blocked rerun must write nothing");
    }

    [Fact]
    public async Task Rerun_with_a_suspendable_node_in_the_closure_is_refused_and_writes_nothing()
    {
        // CanSuspend is part of the effectful gate (re-running a parking node re-stages an external wait / agent
        // run). start → suspendprobe(CanSuspend) → end; rerun from "start" puts suspendprobe in the closure → refused.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var probeKey = "rerun-cansuspend-" + Guid.NewGuid().ToString("N");
        SuspendProbeNode.Reset(probeKey);

        var workflowId = await CreateWorkflowAsync(teamId, userId, SuspendInlineDef(probeKey));
        var originalRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await RunEngineAsync(originalRunId);   // suspends; the gate fires regardless of the original's state

        var before = await RunCountAsync(teamId);
        var ex = await Should.ThrowAsync<RerunBlockedBySideEffectException>(async () =>
            await RerunAsync(originalRunId, "start", teamId, userId));
        ex.BlockedNodeIds.ShouldContain("suspendprobe", "a CanSuspend node in the closure is refused");

        (await RunCountAsync(teamId)).ShouldBe(before, "a suspend-blocked rerun must write nothing");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Workflow definition builders
    // ─────────────────────────────────────────────────────────────────────────────

    // start(manual) → mutator(side-effecting, counts) → transform(json_emit echoes mutator.n) → end(terminal).
    private static WorkflowDefinition MutatorTransformDef(string probeKey) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "mutator", TypeKey = MutatingProbeNode.Key, Config = WorkflowsTestSeed.Json($$"""{"key":"{{probeKey}}"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "transform", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{"n":"{{nodes.mutator.outputs.n}}"}""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "mutator" },
            new() { From = "mutator", To = "transform" },
            new() { From = "transform", To = "end" },
        },
    };

    // start → a → end (all json_emit / terminal — no side effects).
    private static WorkflowDefinition LinearEchoDef() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "a", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{"tag":"a"}""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "a" },
            new() { From = "a", To = "end" },
        },
    };

    // start → a → {b, c} → d(join) → end.
    private static WorkflowDefinition DiamondDef() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "a", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{"tag":"a"}""") },
            new() { Id = "b", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{"tag":"b"}""") },
            new() { Id = "c", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{"tag":"c"}""") },
            new() { Id = "d", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{"tag":"d"}""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "a" },
            new() { From = "a", To = "b" },
            new() { From = "a", To = "c" },
            new() { From = "b", To = "d" },
            new() { From = "c", To = "d" },
            new() { From = "d", To = "end" },
        },
    };

    // start → if(true) → {t (true handle), f (false handle)} → j(join) → end.
    private static WorkflowDefinition BranchJoinDef() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "iff", TypeKey = "logic.if", Config = WorkflowsTestSeed.Json("""{"condition":"{{trigger.branch}} == \"yes\""}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "t", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{"tag":"t"}""") },
            new() { Id = "f", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{"tag":"f"}""") },
            new() { Id = "j", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{"tag":"j"}""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "iff" },
            new() { From = "iff", To = "t", SourceHandle = "true" },
            new() { From = "iff", To = "f", SourceHandle = "false" },
            new() { From = "t", To = "j" },
            new() { From = "f", To = "j" },
            new() { From = "j", To = "end" },
        },
    };

    // start → flaky(always fails) =(error)=> caught → end ; flaky =(normal)=> ok.
    private static WorkflowDefinition FailWithErrorEdgeDef(string flakyKey) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "flaky", TypeKey = FlakyTestNode.Key, Config = WorkflowsTestSeed.Json($$"""{"key":"{{flakyKey}}","failTimes":99}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "ok", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{"tag":"ok"}""") },
            // caught CONSUMES the failed upstream's rebuilt error output — the path the rerun must reconstruct.
            new() { Id = "caught", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{"msg":"{{nodes.flaky.outputs.error.message}}"}""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "flaky" },
            new() { From = "flaky", To = "ok" },
            new() { From = "flaky", To = "caught", SourceHandle = WorkflowHandles.Error },
            new() { From = "caught", To = "end" },
        },
    };

    // start → flaky(always fails) → end. No error edge — the failure fails the run.
    private static WorkflowDefinition FailNoErrorEdgeDef(string flakyKey) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "flaky", TypeKey = FlakyTestNode.Key, Config = WorkflowsTestSeed.Json($$"""{"key":"{{flakyKey}}","failTimes":99}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "flaky" },
            new() { From = "flaky", To = "end" },
        },
    };

    // start → loop(1 pass; body: loop_start → mutator) → transform(json_emit) → end.
    private static WorkflowDefinition LoopBodyDef(string bodyKey) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "loop", TypeKey = "flow.loop", Config = WorkflowsTestSeed.Json("""{"maxIterations":1}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "ls", TypeKey = "flow.loop_start", ParentId = "loop", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "body", TypeKey = MutatingProbeNode.Key, ParentId = "loop", Config = WorkflowsTestSeed.Json($$"""{"key":"{{bodyKey}}"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "transform", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{"tag":"transform"}""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "loop" },
            new() { From = "loop", To = "transform" },
            new() { From = "ls", To = "body" },
            new() { From = "transform", To = "end" },
        },
    };

    // start → a(json_emit echoes {{team.VAR}} as output "v") → b → end.
    private static WorkflowDefinition EchoTeamVarDef(string varName) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "a", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json($$"""{"v":"{{"{{"}}team.{{varName}}{{"}}"}}"}""") },
            new() { Id = "b", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{"v":"{{nodes.a.outputs.v}}"}""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "a" },
            new() { From = "a", To = "b" },
            new() { From = "b", To = "end" },
        },
    };

    // start → suspendprobe(parks an Action wait) → downstream → end.
    private static WorkflowDefinition SuspendThenDownstreamDef(string probeKey) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "suspendprobe", TypeKey = SuspendProbeNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json($$"""{"key":"{{probeKey}}","item":"x"}""") },
            new() { Id = "downstream", TypeKey = JsonEmitNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{"tag":"downstream"}""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "suspendprobe" },
            new() { From = "suspendprobe", To = "downstream" },
            new() { From = "downstream", To = "end" },
        },
    };

    // start → suspendprobe(CanSuspend) → end.
    private static WorkflowDefinition SuspendInlineDef(string probeKey) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "suspendprobe", TypeKey = SuspendProbeNode.Key, Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json($$"""{"key":"{{probeKey}}","item":"x"}""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "suspendprobe" },
            new() { From = "suspendprobe", To = "end" },
        },
    };

    // start → loop(1 pass; body: loop_start → bodyjson — PURE, no side effects) → end. The loop is still
    // refused in a rerun closure by KIND (slice-1 conservatism), independent of its pure body.
    private static WorkflowDefinition PureBodiedLoopDef() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "loop", TypeKey = "flow.loop", Config = WorkflowsTestSeed.Json("""{"maxIterations":1}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "ls", TypeKey = "flow.loop_start", ParentId = "loop", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "bodyjson", TypeKey = JsonEmitNode.Key, ParentId = "loop", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{"tag":"body"}""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "loop" },
            new() { From = "loop", To = "end" },
            new() { From = "ls", To = "bodyjson" },
        },
    };

    // ─────────────────────────────────────────────────────────────────────────────
    //  Run-staging + query helpers
    // ─────────────────────────────────────────────────────────────────────────────

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition def)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CodeSpace.Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "rerun-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = def,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    /// <summary>Seed + walk a fresh authored run to terminal.</summary>
    private async Task<Guid> RunFreshAsync(Guid workflowId, Guid teamId, string payloadJson = "{}")
    {
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: payloadJson);
        await RunEngineAsync(runId);
        return runId;
    }

    /// <summary>Stage a snapshot-origin run (no Workflow row) via the real starter, returning its id (Pending → walk it).</summary>
    private async Task<Guid> StartSnapshotAsync(Guid teamId, Guid userId, WorkflowDefinition def)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IRunFromSnapshotStarter>().StartFromSnapshotAsync(def, teamId, userId, "{}", CancellationToken.None);
    }

    /// <summary>Drive the REAL service seam, then return the new run id (dispatch fires inline post-commit, leaving the fork Enqueued).</summary>
    private async Task<Guid> RerunAsync(Guid originalRunId, string fromNodeId, Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IWorkflowService>().RerunFromNodeAsync(originalRunId, fromNodeId, teamId, userId, CancellationToken.None);
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private async Task SetTeamVarAsync(Guid teamId, Guid userId, string name, string value)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IVariableService>().SetAsync(
            VariableScope.Team, teamId, teamId, name, VariableValueType.String,
            JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone(), null, userId, CancellationToken.None);
    }

    private async Task AssertRunStatusAsync(Guid runId, WorkflowRunStatus expected)
    {
        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.Status.ShouldBe(expected, $"run {runId} status; error={run.Error}");
    }

    private async Task<WorkflowRunNode> LoadCellAsync(Guid runId, string nodeId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunNode.AsNoTracking()
            .SingleAsync(n => n.RunId == runId && n.NodeId == nodeId && n.IterationKey == "");
    }

    private async Task<int> NodeStartedCountAsync(Guid runId, string nodeId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunRecord.AsNoTracking()
            .CountAsync(r => r.RunId == runId && r.NodeId == nodeId && r.IterationKey == "" && r.RecordType == WorkflowRunRecordTypes.NodeStarted);
    }

    private static readonly string[] TerminalRecordTypes =
        { WorkflowRunRecordTypes.NodeCompleted, WorkflowRunRecordTypes.NodeSkipped, WorkflowRunRecordTypes.NodeFailed };

    private async Task<int> TerminalRecordCountAsync(Guid runId, string nodeId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunRecord.AsNoTracking()
            .CountAsync(r => r.RunId == runId && r.NodeId == nodeId && r.IterationKey == "" && TerminalRecordTypes.Contains(r.RecordType));
    }

    private async Task<int> RecordCountAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunRecord.AsNoTracking().CountAsync(r => r.RunId == runId);
    }

    private async Task<int> CellCountAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunNode.AsNoTracking().CountAsync(n => n.RunId == runId);
    }

    private async Task<int> RunCountAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().CountAsync(r => r.TeamId == teamId);
    }
}
