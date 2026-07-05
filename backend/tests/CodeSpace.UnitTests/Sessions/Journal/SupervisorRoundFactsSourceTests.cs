using CodeSpace.Core.Services.Sessions.Journal.FactsSources;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Messages.Agents;
using CodeSpace.UnitTests.Infrastructure;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions.Journal;

/// <summary>
/// 🟢 Unit: the round facts source — tags each supervisor decision step with its 1-based round (the decision's
/// FenceEpoch + 1), keyed by the decision's timeline event id, so the journal reads "round N · …" per step and a terminal
/// "budget exhausted" is a plain consequence of the round count. EVERY supervisor decision gets a round (incl. a no-op
/// spawn, so a wasted round is visible). Driven over the shared in-memory decision log — no database.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorRoundFactsSourceTests
{
    [Fact]
    public async Task Tags_each_supervisor_decision_with_its_1_based_round()
    {
        var runId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var log = new FakeSupervisorDecisionLog();

        log.SeedTerminal(runId, teamId, SupervisorDecisionKinds.Plan, "{}", "{}");
        log.SeedTerminal(runId, teamId, SupervisorDecisionKinds.AskHuman, "{}", "{}");
        log.SeedTerminal(runId, teamId, SupervisorDecisionKinds.Spawn, "{}", "{}");   // even a no-op spawn carries its round

        // FenceEpoch = the 0-based turn number in production (the row is claimed at fenceEpoch: TurnNumber); mirror that.
        for (var i = 0; i < log.Rows.Count; i++) log.Rows[i].FenceEpoch = i;

        var facts = await new SupervisorRoundFactsSource(log).GatherAsync(runId, teamId, CancellationToken.None);

        log.Rows.Select(d => facts[SupervisorDecisionTimelineMap.EventId(d)].Round)
            .ShouldBe(new int?[] { 1, 2, 3 }, "each decision's round is its FenceEpoch + 1 — plan=1, ask=2, spawn=3");
    }

    [Fact]
    public async Task A_run_with_no_supervisor_decisions_adds_nothing()
    {
        (await new SupervisorRoundFactsSource(new FakeSupervisorDecisionLog()).GatherAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None))
            .ShouldBeEmpty("a plain agent run has no supervisor rounds");
    }
}
