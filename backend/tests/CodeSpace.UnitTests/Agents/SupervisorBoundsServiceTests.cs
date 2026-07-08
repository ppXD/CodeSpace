using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.UnitTests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the PR-E E5 bounds + governance driven through the REAL <see cref="SupervisorTurnService"/> pipeline
/// (over an in-memory ledger), proving each bound FORCE-STOPS the run cleanly with its DISTINCT terminal reason
/// — using a decider that would otherwise spawn / plan FOREVER (so the BOUND, not the decider, is what stops it).
/// Also pins the LEDGER-COUNTED property: re-rehydrating the same seeded ledger re-derives the same total-spawn
/// count → the same forced stop (a re-entry can't reset the bound). The real engine + Postgres E2E lives in
/// <c>SupervisorBoundsFlowTests</c>; here the loop logic is pinned DB-free.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorBoundsServiceTests
{
    private readonly Guid _runId = Guid.NewGuid();
    private readonly Guid _teamId = Guid.NewGuid();

    // ── Total-spawn cap force-stops at the limit, counted from the ledger ────────────

    [Fact]
    public async Task The_total_spawn_cap_force_stops_a_decider_that_keeps_spawning()
    {
        var ledger = new FakeSupervisorDecisionLog();

        // Two prior spawn decisions already staged 2 agents each → 4 total spawned (a ledger fact).
        ledger.SeedTerminal(_runId, _teamId, SupervisorDecisionKinds.Spawn, """{"subtaskIds":["a","b"]}""", """{"agentRunIds":["..","ŝ"],"agentCount":2}""");
        ledger.SeedTerminal(_runId, _teamId, SupervisorDecisionKinds.Spawn, """{"subtaskIds":["c","d"]}""", """{"agentCount":2}""");

        // Cap = 5; the AlwaysSpawnDecider wants 2 more → 4 + 2 = 6 > 5 → refused → force-STOP.
        var result = await Service(ledger, new AlwaysSpawnDecider()).RunTurnAsync(_runId, _teamId, "sup", "g", null, Config(maxTotalSpawns: 5), CancellationToken.None);

        result.IsFinished.ShouldBeTrue("the total-spawn cap — not the decider — stops the run");
        result.TerminalReason.ShouldBe(SupervisorStopReasons.TotalSpawnCapReached);

        // The forced stop recorded a terminal row; the cap was NOT exceeded (no new spawn executed).
        ledger.Rows.Count(r => r.DecisionKind == SupervisorDecisionKinds.Spawn).ShouldBe(2, "no third spawn executed — the cap held");
    }

    [Fact]
    public async Task The_total_spawn_count_is_ledger_counted_and_survives_a_re_entry()
    {
        var ledger = new FakeSupervisorDecisionLog();
        ledger.SeedTerminal(_runId, _teamId, SupervisorDecisionKinds.Spawn, """{"subtaskIds":["a","b","c"]}""", """{"agentCount":3}""");

        var service = Service(ledger, new AlwaysSpawnDecider());

        // Two independent rehydrates of the SAME seeded ledger fold the SAME total — the counter is a ledger
        // fact, NOT an in-memory tally, so a re-entry can't reset it.
        var first = await service.RehydrateFromDecisionLogAsync(_runId, _teamId, "sup", "g", Config(maxTotalSpawns: 3), CancellationToken.None);
        var second = await service.RehydrateFromDecisionLogAsync(_runId, _teamId, "sup", "g", Config(maxTotalSpawns: 3), CancellationToken.None);

        first.TotalSpawnedAgents.ShouldBe(3);
        second.TotalSpawnedAgents.ShouldBe(3, "a re-entry re-derives the SAME total from the durable tape");

        // And with the cap already met, the next spawn turn force-STOPs (the bound can't be sidestepped by re-entering).
        var result = await service.RunTurnAsync(_runId, _teamId, "sup", "g", null, Config(maxTotalSpawns: 3), CancellationToken.None);
        result.TerminalReason.ShouldBe(SupervisorStopReasons.TotalSpawnCapReached);
    }

    // ── No-progress guard force-stops a decider that loops without progress ──────────

    [Fact]
    public async Task The_no_progress_guard_force_stops_a_decider_that_only_plans()
    {
        var ledger = new FakeSupervisorDecisionLog();

        // 3 prior plan decisions made no agent progress → the no-progress streak is 3 == the cap.
        for (var i = 0; i < 3; i++) ledger.SeedTerminal(_runId, _teamId, SupervisorDecisionKinds.Plan, $$"""{"t":{{i}}}""", "{}");

        var result = await Service(ledger, new AlwaysPlanDecider()).RunTurnAsync(_runId, _teamId, "sup", "g", null, Config(maxNoProgress: 3), CancellationToken.None);

        result.IsFinished.ShouldBeTrue("the best-effort no-progress guard stops a plan-forever decider");
        result.TerminalReason.ShouldBe(SupervisorStopReasons.NoProgress);
    }

    [Fact]
    public async Task The_no_progress_guard_force_stops_a_plan_merge_loop_that_re_merges_the_same_work()
    {
        // The runaway a supervised run MUST self-terminate now that there is no round budget: the decider alternates
        // plan → merge forever WITHOUT spawning (so the total-spawn / cost caps never trip). The FIRST merge of an
        // agent-run id is progress; a merge that re-consolidates only ALREADY-merged ids makes no fresh progress — so
        // the streak still climbs to the cap. A regression (treating every merge as progress) reopens an infinite loop.
        var ledger = new FakeSupervisorDecisionLog();
        var agentId = Guid.NewGuid();
        var reMerge = $$"""{"merged":[{"agentRunId":"{{agentId}}"}],"count":1}""";

        // merge #1 of the id resets the streak; the following plan + re-merges of the SAME id make no fresh progress → streak 4 == the cap.
        ledger.SeedTerminal(_runId, _teamId, SupervisorDecisionKinds.Merge, "{}", reMerge);
        ledger.SeedTerminal(_runId, _teamId, SupervisorDecisionKinds.Plan, "{}", "{}");
        ledger.SeedTerminal(_runId, _teamId, SupervisorDecisionKinds.Merge, "{}", reMerge);
        ledger.SeedTerminal(_runId, _teamId, SupervisorDecisionKinds.Plan, "{}", "{}");
        ledger.SeedTerminal(_runId, _teamId, SupervisorDecisionKinds.Merge, "{}", reMerge);

        var result = await Service(ledger, new AlwaysPlanDecider()).RunTurnAsync(_runId, _teamId, "sup", "g", null, Config(maxNoProgress: 4), CancellationToken.None);

        result.IsFinished.ShouldBeTrue("a plan/merge loop that re-merges the same work self-terminates on the no-progress guard");
        result.TerminalReason.ShouldBe(SupervisorStopReasons.NoProgress);
    }

    [Fact]
    public async Task A_merge_that_integrates_new_work_each_turn_never_trips_the_no_progress_guard()
    {
        // The legitimate counterpart: a merge that consolidates a DISTINCT (newly-produced) agent-run id is real
        // progress and resets the streak — so a healthy spawn→merge run is never falsely stopped. More new-id merges
        // than the cap must NOT fire the guard (the fix gates on NEW ids, not merge-verb alone).
        var ledger = new FakeSupervisorDecisionLog();
        for (var i = 0; i < 5; i++)
            ledger.SeedTerminal(_runId, _teamId, SupervisorDecisionKinds.Merge, "{}", $$"""{"merged":[{"agentRunId":"{{Guid.NewGuid()}}"}],"count":1}""");

        var result = await Service(ledger, new AlwaysPlanDecider()).RunTurnAsync(_runId, _teamId, "sup", "g", null, Config(maxNoProgress: 2), CancellationToken.None);

        result.DecisionKind.ShouldBe(SupervisorDecisionKinds.Plan, "a merge that integrates NEW work each turn is progress — the guard must not false-fire, so the decider is still asked");
    }

    // ── Governance: a Spawns policy rewrites the spawn into an ask_human approval park ──

    [Fact]
    public async Task A_spawns_policy_parks_the_spawn_for_a_human_instead_of_creating_agents()
    {
        var ledger = new FakeSupervisorDecisionLog();
        // A prior plan exists so the spawn turn (turn 1) has subtasks; the decider always spawns.
        ledger.SeedTerminal(_runId, _teamId, SupervisorDecisionKinds.Plan, """{"subtasks":[{"id":"a","title":"A","instruction":"do"}]}""", "{}");

        var executor = new CountingExecutor();
        var service = new SupervisorTurnService(ledger, new AlwaysSpawnDecider(), executor, db: null!, new FakeAcceptanceGrader(), new FakeDecisionQueue(), new FakeDecisionArbiter(), new FakeDecisionAnswerService(), new FakeWorkPlanStore(), null!, null!, null!, NullLogger<SupervisorTurnService>.Instance);

        var result = await service.RunTurnAsync(_runId, _teamId, "sup", "g", null, Config(approvalPolicy: "spawns"), CancellationToken.None);

        result.IsFinished.ShouldBeFalse("the gated spawn does NOT finish — it parks for a human");
        result.DecisionKind.ShouldBe(SupervisorDecisionKinds.AskHuman, "the spawn was rewritten into an approval ask_human");

        // The recorded decision is the ask_human, NOT a spawn — so NO agent was created.
        ledger.Rows.Count(r => r.DecisionKind == SupervisorDecisionKinds.Spawn).ShouldBe(0, "no spawn ran — the human gates it first");
        ledger.Rows.ShouldContain(r => r.DecisionKind == SupervisorDecisionKinds.AskHuman);
    }

    [Fact]
    public async Task A_none_policy_spawns_without_a_gate()
    {
        var ledger = new FakeSupervisorDecisionLog();
        ledger.SeedTerminal(_runId, _teamId, SupervisorDecisionKinds.Plan, """{"subtasks":[{"id":"a","title":"A","instruction":"do"}]}""", "{}");

        var executor = new CountingExecutor();
        var service = new SupervisorTurnService(ledger, new AlwaysSpawnDecider(), executor, db: null!, new FakeAcceptanceGrader(), new FakeDecisionQueue(), new FakeDecisionArbiter(), new FakeDecisionAnswerService(), new FakeWorkPlanStore(), null!, null!, null!, NullLogger<SupervisorTurnService>.Instance);

        var result = await service.RunTurnAsync(_runId, _teamId, "sup", "g", null, Config(approvalPolicy: "none"), CancellationToken.None);

        result.DecisionKind.ShouldBe(SupervisorDecisionKinds.Spawn, "None policy → the spawn proceeds ungated");
        executor.Calls.ShouldBe(1, "the spawn executor ran");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────

    private SupervisorTurnService Service(FakeSupervisorDecisionLog ledger, ISupervisorDecider decider) =>
        new(ledger, decider, new CountingExecutor(), db: null!, new FakeAcceptanceGrader(), new FakeDecisionQueue(), new FakeDecisionArbiter(), new FakeDecisionAnswerService(), new FakeWorkPlanStore(), null!, null!, null!, NullLogger<SupervisorTurnService>.Instance);

    private static SupervisorGoalConfig Config(int? maxTotalSpawns = null, int? maxNoProgress = null, string? approvalPolicy = null) =>
        new() { MaxTotalSpawns = maxTotalSpawns, MaxNoProgressDecisions = maxNoProgress, ApprovalPolicy = approvalPolicy };

    /// <summary>A decider that always spawns 2 subtasks — proves a BOUND (not the decider) stops a runaway spawn loop.</summary>
    private sealed class AlwaysSpawnDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new SupervisorDecision
            {
                Kind = SupervisorDecisionKinds.Spawn,
                PayloadJson = JsonSerializer.Serialize(new SupervisorSpawnPayload { SubtaskIds = new[] { "a", "b" } }, AgentJson.Options),
            });
    }

    /// <summary>A decider that always plans — proves the no-progress / round bounds stop a plan-forever decider.</summary>
    private sealed class AlwaysPlanDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new SupervisorDecision { Kind = SupervisorDecisionKinds.Plan, PayloadJson = """{"x":1}""" });
    }

    /// <summary>A synchronous executor that records each call's outcome (spawn → 2 staged agents, else synchronous) and counts invocations.</summary>
    private sealed class CountingExecutor : ISupervisorActionExecutor
    {
        public int Calls { get; private set; }

        public Task<SupervisorExecution> ExecuteAsync(SupervisorDecision decision, SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            Calls++;

            if (decision.Kind is SupervisorDecisionKinds.Spawn or SupervisorDecisionKinds.Retry)
                return Task.FromResult(SupervisorExecution.ParkedOnAgents("""{"agentCount":2}""", 2));

            if (decision.Kind == SupervisorDecisionKinds.AskHuman)
                return Task.FromResult(SupervisorExecution.ParkedOnHuman("""{"askHumanToken":"t"}""", "t"));

            return Task.FromResult(SupervisorExecution.Synchronous("{}"));
        }
    }
}
