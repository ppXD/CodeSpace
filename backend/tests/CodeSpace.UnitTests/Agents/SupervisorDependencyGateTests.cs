using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the pure dependency-ordering rail (<see cref="SupervisorDependencyGate"/>) — the build-graph's "explicit
/// dependency" made executable. Pins: a flat plan admits every requested subtask verbatim (byte-identical); a subtask is
/// DEFERRED until every DependsOn is a NON-REJECTED success (and only then ready); a rejected / failed dependency keeps
/// dependents blocked; multi-dependency waits for all; a retry that succeeds satisfies (latest attempt wins, so a later
/// rejection re-blocks); a cycle never becomes ready (converges to a no-progress stop, never an infinite loop); and the
/// decider FRONTIER lists ready vs blocked, excluding done.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorDependencyGateTests
{
    // ── Partition: the server's spawn clamp ────────────────────────────────────────────

    [Fact]
    public void A_flat_plan_admits_every_requested_subtask_verbatim()
    {
        var (ready, deferred) = SupervisorDependencyGate.Partition(Context(Plan(("a", null), ("b", null))), new[] { "a", "b" });

        ready.ShouldBe(new[] { "a", "b" }, "no DependsOn → every requested subtask is ready (byte-identical to pre-slice)");
        deferred.ShouldBeEmpty();
    }

    [Fact]
    public void A_subtask_whose_dependency_is_not_done_is_deferred()
    {
        var (ready, deferred) = SupervisorDependencyGate.Partition(Context(Plan(("a", null), ("b", new[] { "a" }))), new[] { "a", "b" });

        ready.ShouldBe(new[] { "a" }, "a has no deps → ready; b waits on the not-yet-run a");
        deferred.ShouldBe(new[] { "b" });
    }

    [Theory]
    [InlineData("Succeeded", null, true)]   // ungraded success → a usable contract
    [InlineData("Succeeded", true, true)]   // accepted success → satisfied
    [InlineData("Succeeded", false, false)] // objectively REJECTED → not a usable contract
    [InlineData("Failed", null, false)]     // failed → not satisfied
    public void A_dependency_is_satisfied_only_by_a_non_rejected_success(string status, bool? accepted, bool expectReady)
    {
        var ctx = Context(Plan(("a", null), ("b", new[] { "a" })), Spawn((("a", status, accepted))));

        var (ready, deferred) = SupervisorDependencyGate.Partition(ctx, new[] { "b" });

        if (expectReady) ready.ShouldBe(new[] { "b" }, "a is a non-rejected success → b is ready");
        else deferred.ShouldBe(new[] { "b" }, "a is not a usable contract → b stays blocked");
    }

    [Fact]
    public void A_multi_dependency_subtask_waits_for_every_dependency()
    {
        var plan = Plan(("a", null), ("b", null), ("c", new[] { "a", "b" }));

        SupervisorDependencyGate.Partition(Context(plan, Spawn(("a", "Succeeded", null))), new[] { "c" })
            .Deferred.ShouldBe(new[] { "c" }, "only a is done → c still waits on b");

        SupervisorDependencyGate.Partition(Context(plan, Spawn(("a", "Succeeded", null), ("b", "Succeeded", null))), new[] { "c" })
            .Ready.ShouldBe(new[] { "c" }, "both a and b are done → c is ready");
    }

    [Fact]
    public void A_retry_that_succeeds_satisfies_the_dependency()
    {
        var ctx = Context(Plan(("a", null), ("b", new[] { "a" })),
            Spawn(("a", "Failed", null)),     // the original attempt failed
            Retry(("a", "Succeeded", null))); // the retry succeeded → a is now satisfied

        SupervisorDependencyGate.Partition(ctx, new[] { "b" }).Ready.ShouldBe(new[] { "b" });
    }

    [Fact]
    public void The_latest_attempt_wins_a_later_rejection_re_blocks_dependents()
    {
        var ctx = Context(Plan(("a", null), ("b", new[] { "a" })),
            Spawn(("a", "Succeeded", true)),  // accepted...
            Retry(("a", "Succeeded", false))); // ...then a later attempt is rejected → latest wins → not satisfied

        SupervisorDependencyGate.Partition(ctx, new[] { "b" }).Deferred.ShouldBe(new[] { "b" });
    }

    [Fact]
    public void A_cycle_never_becomes_ready()
    {
        var (ready, deferred) = SupervisorDependencyGate.Partition(Context(Plan(("a", new[] { "b" }), ("b", new[] { "a" }))), new[] { "a", "b" });

        ready.ShouldBeEmpty("a cyclic DAG never satisfies — the run converges to a no-progress stop, never an infinite loop");
        deferred.ShouldBe(new[] { "a", "b" });
    }

    [Fact]
    public void Partition_preserves_request_order()
    {
        var ctx = Context(Plan(("a", null), ("b", new[] { "a" }), ("c", null)), Spawn(("a", "Succeeded", null)));

        SupervisorDependencyGate.Partition(ctx, new[] { "c", "b", "a" }).Ready.ShouldBe(new[] { "c", "b", "a" }, "all ready (a done) → request order preserved");
    }

    // ── Frontier: the decider's guidance ───────────────────────────────────────────────

    [Fact]
    public void The_frontier_lists_ready_and_blocked_excluding_done()
    {
        var ctx = Context(Plan(("a", null), ("b", new[] { "a" }), ("c", new[] { "b" })), Spawn(("a", "Succeeded", null)));

        var (ready, blocked) = SupervisorDependencyGate.Frontier(ctx);

        ready.ShouldBe(new[] { "b" }, "a is done (excluded); b's dep a is satisfied → ready");
        blocked.Select(x => x.Id).ShouldBe(new[] { "c" }, "c waits on the not-done b");
        blocked.Single().WaitingOn.ShouldBe(new[] { "b" });
    }

    [Fact]
    public void The_frontier_is_empty_for_a_flat_plan()
    {
        var (ready, blocked) = SupervisorDependencyGate.Frontier(Context(Plan(("a", null), ("b", null))));

        ready.ShouldBeEmpty();
        blocked.ShouldBeEmpty();
    }

    // ── Helpers ───

    private static SupervisorTurnContext Context(params SupervisorPriorDecision[] prior) => new() { Goal = "g", PriorDecisions = prior };

    private static SupervisorPriorDecision Plan(params (string Id, string[]? DependsOn)[] subtasks)
    {
        var payload = JsonSerializer.Serialize(new
        {
            goal = "g",
            subtasks = subtasks.Select(s => s.DependsOn is null
                ? (object)new { id = s.Id, title = s.Id, instruction = "do" }
                : new { id = s.Id, title = s.Id, instruction = "do", dependsOn = s.DependsOn }),
        }, AgentJson.Options);

        return Prior(SupervisorDecisionKinds.Plan, payload, "{}");
    }

    private static SupervisorPriorDecision Spawn(params (string SubtaskId, string Status, bool? Accepted)[] units)
    {
        var results = units.Select(u => Result(u.Status, u.Accepted)).ToArray();
        var payload = JsonSerializer.Serialize(new { subtaskIds = units.Select(u => u.SubtaskId).ToArray() }, AgentJson.Options);
        return Prior(SupervisorDecisionKinds.Spawn, payload, Outcome(results));
    }

    private static SupervisorPriorDecision Retry((string SubtaskId, string Status, bool? Accepted) unit)
    {
        var result = Result(unit.Status, unit.Accepted);
        var payload = JsonSerializer.Serialize(new { subtaskId = unit.SubtaskId }, AgentJson.Options);
        return Prior(SupervisorDecisionKinds.Retry, payload, Outcome(new[] { result }));
    }

    private static SupervisorAgentResult Result(string status, bool? accepted) =>
        new() { AgentRunId = Guid.NewGuid(), Status = status, ProducedBranch = "b", AcceptancePassed = accepted };

    private static string Outcome(SupervisorAgentResult[] results) =>
        SupervisorOutcome.FoldAgentResults(
            JsonSerializer.Serialize(new { agentRunIds = results.Select(r => r.AgentRunId).ToArray(), agentCount = results.Length }, AgentJson.Options), results);

    private static SupervisorPriorDecision Prior(string kind, string payload, string outcome) =>
        new() { Id = Guid.NewGuid(), Sequence = 1, DecisionKind = kind, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = payload, OutcomeJson = outcome };
}
