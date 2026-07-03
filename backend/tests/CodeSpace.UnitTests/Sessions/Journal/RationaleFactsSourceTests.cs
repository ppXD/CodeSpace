using CodeSpace.Core.Services.Sessions.Journal.FactsSources;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Messages.Agents;
using CodeSpace.UnitTests.Infrastructure;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions.Journal;

/// <summary>
/// 🟢 Unit: the rationale facts source — the supervisor's authored "why" enrichment. Pins the one-line FORMAT (reason ·
/// Evidence: …, either half optional, both-empty → null) and the GATHER contract (a decision that carries a rationale is
/// keyed by its timeline event id so the walk matches it to the same step; a decision with none contributes nothing).
/// Driven over the shared in-memory decision log — no database.
/// </summary>
[Trait("Category", "Unit")]
public class RationaleFactsSourceTests
{
    [Theory]
    [InlineData("Spawned to unblock", "CI is red", "Spawned to unblock · Evidence: CI is red")]
    [InlineData("Spawned to unblock", null, "Spawned to unblock")]
    [InlineData(null, "CI is red", "Evidence: CI is red")]
    [InlineData("  ", "  ", null)]
    [InlineData(null, null, null)]
    public void Formats_the_rationale_line(string? why, string? evidence, string? expected)
    {
        RationaleFactsSource.FormatRationale((why, evidence)).ShouldBe(expected);
    }

    [Fact]
    public async Task Keys_each_rationale_by_its_decision_step_id()
    {
        var runId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var log = new FakeSupervisorDecisionLog();

        log.SeedTerminal(runId, teamId, SupervisorDecisionKinds.Spawn, Payload("Fan out the work", "3 independent files"), "{}");

        var facts = await new RationaleFactsSource(log).GatherAsync(runId, teamId, CancellationToken.None);

        var decision = log.Rows.Single();
        facts.ShouldContainKey(SupervisorDecisionTimelineMap.EventId(decision));
        facts[SupervisorDecisionTimelineMap.EventId(decision)].Rationale.ShouldBe("Fan out the work · Evidence: 3 independent files");
    }

    [Fact]
    public async Task A_decision_with_no_rationale_contributes_nothing()
    {
        var runId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var log = new FakeSupervisorDecisionLog();

        log.SeedTerminal(runId, teamId, SupervisorDecisionKinds.Plan, "{}", "{}");   // no rationale at the payload root

        (await new RationaleFactsSource(log).GatherAsync(runId, teamId, CancellationToken.None))
            .ShouldBeEmpty("a decision the model authored no rationale for adds no facts — most steps stay bare");
    }

    [Fact]
    public async Task Surfaces_the_rationale_uniformly_across_every_verb_not_just_retry()
    {
        // The rationale lives at the payload root for EVERY verb (SupervisorOutcome.ReadRationale), so this one source
        // enriches plan/spawn/stop alike — the chain-of-thought is surfaced across the whole trajectory, not only retries.
        var runId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var log = new FakeSupervisorDecisionLog();

        log.SeedTerminal(runId, teamId, SupervisorDecisionKinds.Plan, Payload("Break into 3 tasks", null), "{}");
        log.SeedTerminal(runId, teamId, SupervisorDecisionKinds.Stop, Payload("All checks pass", null), "{}");

        var facts = await new RationaleFactsSource(log).GatherAsync(runId, teamId, CancellationToken.None);

        facts.Count.ShouldBe(2, "both the plan and the stop carry their authored rationale");
        facts.Values.Select(f => f.Rationale).ShouldBe(new[] { "Break into 3 tasks", "All checks pass" }, ignoreOrder: true);
    }

    private static string Payload(string? why, string? evidence) =>
        System.Text.Json.JsonSerializer.Serialize(new { rationale = new { why, evidence } });
}
