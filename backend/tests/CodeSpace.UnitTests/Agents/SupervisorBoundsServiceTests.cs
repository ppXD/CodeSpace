using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Executors;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Agents;
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

    // ── Round budget from the config force-stops (config tighter than the default) ────

    [Fact]
    public async Task The_config_max_rounds_force_stops_before_the_decider_is_asked()
    {
        var ledger = new FakeLedger();

        // Seed 3 decided decisions so TurnNumber == 3 == the configured MaxRounds.
        for (var i = 0; i < 3; i++) ledger.SeedTerminal(_runId, _teamId, SupervisorDecisionKinds.Plan, $$"""{"t":{{i}}}""", "{}");

        var result = await Service(ledger, new AlwaysSpawnDecider()).RunTurnAsync(_runId, _teamId, "sup", "g", null, Config(maxRounds: 3), CancellationToken.None);

        result.IsFinished.ShouldBeTrue("the config round budget — not the never-stopping decider — terminates");
        result.TerminalReason.ShouldBe(SupervisorStopReasons.BudgetExhausted);
    }

    // ── Total-spawn cap force-stops at the limit, counted from the ledger ────────────

    [Fact]
    public async Task The_total_spawn_cap_force_stops_a_decider_that_keeps_spawning()
    {
        var ledger = new FakeLedger();

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
        var ledger = new FakeLedger();
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
        var ledger = new FakeLedger();

        // 3 prior plan decisions made no agent progress → the no-progress streak is 3 == the cap.
        for (var i = 0; i < 3; i++) ledger.SeedTerminal(_runId, _teamId, SupervisorDecisionKinds.Plan, $$"""{"t":{{i}}}""", "{}");

        var result = await Service(ledger, new AlwaysPlanDecider()).RunTurnAsync(_runId, _teamId, "sup", "g", null, Config(maxNoProgress: 3), CancellationToken.None);

        result.IsFinished.ShouldBeTrue("the best-effort no-progress guard stops a plan-forever decider");
        result.TerminalReason.ShouldBe(SupervisorStopReasons.NoProgress);
    }

    // ── Governance: a Spawns policy rewrites the spawn into an ask_human approval park ──

    [Fact]
    public async Task A_spawns_policy_parks_the_spawn_for_a_human_instead_of_creating_agents()
    {
        var ledger = new FakeLedger();
        // A prior plan exists so the spawn turn (turn 1) has subtasks; the decider always spawns.
        ledger.SeedTerminal(_runId, _teamId, SupervisorDecisionKinds.Plan, """{"subtasks":[{"id":"a","title":"A","instruction":"do"}]}""", "{}");

        var executor = new CountingExecutor();
        var service = new SupervisorTurnService(ledger, new AlwaysSpawnDecider(), executor, db: null!, NullLogger<SupervisorTurnService>.Instance);

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
        var ledger = new FakeLedger();
        ledger.SeedTerminal(_runId, _teamId, SupervisorDecisionKinds.Plan, """{"subtasks":[{"id":"a","title":"A","instruction":"do"}]}""", "{}");

        var executor = new CountingExecutor();
        var service = new SupervisorTurnService(ledger, new AlwaysSpawnDecider(), executor, db: null!, NullLogger<SupervisorTurnService>.Instance);

        var result = await service.RunTurnAsync(_runId, _teamId, "sup", "g", null, Config(approvalPolicy: "none"), CancellationToken.None);

        result.DecisionKind.ShouldBe(SupervisorDecisionKinds.Spawn, "None policy → the spawn proceeds ungated");
        executor.Calls.ShouldBe(1, "the spawn executor ran");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────

    private SupervisorTurnService Service(FakeLedger ledger, ISupervisorDecider decider) =>
        new(ledger, decider, new CountingExecutor(), db: null!, NullLogger<SupervisorTurnService>.Instance);

    private static SupervisorGoalConfig Config(int? maxRounds = null, int? maxTotalSpawns = null, int? maxNoProgress = null, string? approvalPolicy = null) =>
        new() { MaxRounds = maxRounds, MaxTotalSpawns = maxTotalSpawns, MaxNoProgressDecisions = maxNoProgress, ApprovalPolicy = approvalPolicy };

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

    /// <summary>An in-memory ledger (mirrors SupervisorTurnServiceTests.FakeLedger) — the E5 bounds read folded ledger facts off this.</summary>
    private sealed class FakeLedger : ISupervisorDecisionLog
    {
        public List<SupervisorDecisionRecord> Rows { get; } = new();
        private long _seq;

        public void SeedTerminal(Guid runId, Guid teamId, string kind, string payloadJson, string outcomeJson) =>
            Rows.Add(new SupervisorDecisionRecord { Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId, Sequence = ++_seq, DecisionKind = kind, IdempotencyKey = $"{kind}:{Rows.Count}", InputHash = "h", PayloadJson = payloadJson, Status = SupervisorDecisionStatus.Succeeded, OutcomeJson = outcomeJson });

        public Task<SupervisorDecisionClaim> TryClaimAsync(Guid supervisorRunId, Guid teamId, string decisionKind, string idempotencyKey, string inputHash, string payloadJson, long fenceEpoch, CancellationToken cancellationToken)
        {
            var existing = Rows.FirstOrDefault(r => r.SupervisorRunId == supervisorRunId && r.IdempotencyKey == idempotencyKey);

            if (existing != null)
                return Task.FromResult(SupervisorDecisionStateMachine.IsTerminal(existing.Status)
                    ? SupervisorDecisionClaim.Duplicate(existing.Id, existing.Status, existing.OutcomeJson, existing.Error)
                    : SupervisorDecisionClaim.InFlight(existing.Id));

            var row = new SupervisorDecisionRecord { Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = supervisorRunId, Sequence = ++_seq, DecisionKind = decisionKind, IdempotencyKey = idempotencyKey, InputHash = inputHash, PayloadJson = payloadJson, Status = SupervisorDecisionStatus.Pending, FenceEpoch = fenceEpoch };
            Rows.Add(row);
            return Task.FromResult(SupervisorDecisionClaim.Proceed(row.Id));
        }

        public Task<bool> TryBeginExecutionAsync(Guid decisionId, Guid teamId, CancellationToken cancellationToken)
        {
            var row = Rows.Single(r => r.Id == decisionId);
            if (row.Status != SupervisorDecisionStatus.Pending) return Task.FromResult(false);
            row.Status = SupervisorDecisionStatus.Running;
            return Task.FromResult(true);
        }

        public Task RecordTerminalAsync(Guid decisionId, Guid teamId, SupervisorDecisionStatus status, string? outcomeJson, string? error, CancellationToken cancellationToken)
        {
            var row = Rows.Single(r => r.Id == decisionId);
            row.Status = status;
            row.OutcomeJson = outcomeJson;
            row.Error = error;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SupervisorDecisionRecord>> GetForRunAsync(Guid supervisorRunId, Guid teamId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SupervisorDecisionRecord>>(Rows.Where(r => r.SupervisorRunId == supervisorRunId && r.TeamId == teamId).OrderBy(r => r.Sequence).ToList());

        public Task UpdateOutcomeAsync(Guid decisionId, Guid teamId, string foldedOutcomeJson, CancellationToken cancellationToken)
        {
            var row = Rows.SingleOrDefault(r => r.Id == decisionId && r.TeamId == teamId);
            if (row != null) row.OutcomeJson = foldedOutcomeJson;
            return Task.CompletedTask;
        }

        public Task<int> ExpireStalePendingAsync(DateTimeOffset olderThan, CancellationToken cancellationToken) => Task.FromResult(0);
    }
}
