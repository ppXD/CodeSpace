using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Sessions.Journal.FactsSources;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Messages.Agents;
using CodeSpace.UnitTests.Infrastructure;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions.Journal;

/// <summary>
/// 🟢 Unit: the spawn-frontier facts source — the dependency-gated "waiting on #n" (the plan's blocked FRONTIER) shown at
/// a wave. Driven over the shared in-memory decision log, it replays the REAL <see cref="SupervisorDependencyGate.Frontier"/>
/// as of each spawn: a DAG plan surfaces a blocked subtask (with the dep it waits on) EVEN WHEN this spawn didn't request
/// it (frontier, not per-spawn partition), a flat plan surfaces nothing, and a LATER wave — once the dependency is an
/// accepted success — surfaces nothing (the frontier cleared). No database.
/// </summary>
[Trait("Category", "Unit")]
public class SpawnFrontierFactsSourceTests
{
    [Fact]
    public async Task The_deferred_set_is_the_plan_frontier_not_what_this_spawn_requested()
    {
        // THE semantic pin (distinguishes frontier from partition): the spawn requests ONLY a — under a per-spawn
        // "requested minus ready" partition it would defer NOTHING (a is ready). But the journal shows the plan's blocked
        // FRONTIER: b is planned, its dep a isn't an accepted success yet, so b is blocked at this wave → surfaced. If the
        // source ever switched to Partition, this returns empty and the assertion fails.
        var runId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var log = new FakeSupervisorDecisionLog();

        log.SeedTerminal(runId, teamId, SupervisorDecisionKinds.Plan, PlanPayload(("a", null), ("b", new[] { "a" })), "{}");
        log.SeedTerminal(runId, teamId, SupervisorDecisionKinds.Spawn, SpawnPayload("a"), "{}");   // requests ONLY a

        var facts = await new SpawnFrontierFactsSource(log).GatherAsync(runId, teamId, CancellationToken.None);

        var spawn = log.Rows.Single(r => r.DecisionKind == SupervisorDecisionKinds.Spawn);
        var deferred = facts[SupervisorDecisionTimelineMap.EventId(spawn)].Deferred!;
        deferred.Select(d => d.SubtaskId).ShouldBe(new[] { "b" }, "b is in the plan's blocked frontier (dep a not accepted yet) — surfaced though this spawn requested only a");
        deferred.Single().WaitingOn.ShouldBe(new[] { "a" });
    }

    [Fact]
    public async Task A_flat_plan_defers_nothing()
    {
        var runId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var log = new FakeSupervisorDecisionLog();

        log.SeedTerminal(runId, teamId, SupervisorDecisionKinds.Plan, PlanPayload(("a", null), ("b", null)), "{}");
        log.SeedTerminal(runId, teamId, SupervisorDecisionKinds.Spawn, SpawnPayload("a", "b"), "{}");

        (await new SpawnFrontierFactsSource(log).GatherAsync(runId, teamId, CancellationToken.None))
            .ShouldBeEmpty("a flat plan has no dependency edges → nothing is ever blocked → the spawn stays bare");
    }

    [Fact]
    public async Task A_later_wave_surfaces_nothing_once_the_dependency_is_accepted()
    {
        // Two waves over a DAG a→b: at wave 1 (a running) b is blocked → surfaced; at wave 2 (after a succeeded+accepted)
        // the frontier is clear → nothing surfaced. Proves the replay reads the PRIOR wave's folded outcome for satisfied,
        // AND that the frontier is computed per-spawn "as of" its own sequence (not one global set for the whole run).
        var runId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var log = new FakeSupervisorDecisionLog();

        log.SeedTerminal(runId, teamId, SupervisorDecisionKinds.Plan, PlanPayload(("a", null), ("b", new[] { "a" })), "{}");
        log.SeedTerminal(runId, teamId, SupervisorDecisionKinds.Spawn, SpawnPayload("a"), SpawnOutcome(("a", "Succeeded", true)));
        log.SeedTerminal(runId, teamId, SupervisorDecisionKinds.Spawn, SpawnPayload("b"), "{}");

        var facts = await new SpawnFrontierFactsSource(log).GatherAsync(runId, teamId, CancellationToken.None);

        var spawns = log.Rows.Where(r => r.DecisionKind == SupervisorDecisionKinds.Spawn).OrderBy(r => r.Sequence).ToList();
        facts[SupervisorDecisionTimelineMap.EventId(spawns[0])].Deferred!.Single().SubtaskId.ShouldBe("b", "at wave 1 b is blocked (a not yet an accepted success)");
        facts.ShouldNotContainKey(SupervisorDecisionTimelineMap.EventId(spawns[1]), "at wave 2 nothing is blocked — a is now an accepted success, so b is ready");
    }

    private static string PlanPayload(params (string Id, string[]? DependsOn)[] subtasks) =>
        JsonSerializer.Serialize(new
        {
            goal = "g",
            subtasks = subtasks.Select(s => s.DependsOn is null
                ? (object)new { id = s.Id, title = s.Id, instruction = "do" }
                : new { id = s.Id, title = s.Id, instruction = "do", dependsOn = s.DependsOn }),
        }, AgentJson.Options);

    private static string SpawnPayload(params string[] subtaskIds) =>
        JsonSerializer.Serialize(new { subtaskIds }, AgentJson.Options);

    private static string SpawnOutcome(params (string SubtaskId, string Status, bool? Accepted)[] units)
    {
        var results = units.Select(u => new SupervisorAgentResult { AgentRunId = Guid.NewGuid(), Status = u.Status, ProducedBranch = "b", AcceptancePassed = u.Accepted }).ToArray();

        return SupervisorOutcome.FoldAgentResults(
            JsonSerializer.Serialize(new { agentRunIds = results.Select(r => r.AgentRunId).ToArray(), agentCount = results.Length }, AgentJson.Options), results);
    }
}
