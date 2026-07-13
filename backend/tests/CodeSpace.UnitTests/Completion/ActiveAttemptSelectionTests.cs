using CodeSpace.Core.Services.Completion;
using CodeSpace.Messages.Agents;
using Shouldly;
using System.Text.Json;

namespace CodeSpace.UnitTests.Completion;

/// <summary>
/// 🟢 Unit: THE active-attempt selector (P1 identity) — latest-staged-terminal-attempt-wins, pinned in its single
/// home so the completion composer and P+'s executable-set machinery can never invent divergent "which attempt
/// counts" rules. The kernel folds every receipt worst-first by design, so a retried-then-passed unit reads
/// Failed unless the composer pre-filters through THIS selector — these pins are what make that contract real.
/// </summary>
[Trait("Category", "Unit")]
public class ActiveAttemptSelectionTests
{
    [Fact]
    public void A_retried_then_passed_unit_selects_the_passing_attempt()
    {
        var decisions = new[]
        {
            Spawn(seq: 1, new[] { "s1" }, Result("s1-attempt1", "Failed")),
            Retry(seq: 2, "s1", Result("s1-attempt2", "Succeeded")),
        };

        var active = ActiveAttemptSelection.SelectActive(decisions);

        active["s1"].Status.ShouldBe("Succeeded", "the superseded failure's evidence must never reach a fold");
        active["s1"].Summary.ShouldBe("s1-attempt2");
    }

    [Fact]
    public void Selection_orders_by_ledger_sequence_not_list_order()
    {
        var decisions = new[]
        {
            Retry(seq: 5, "s1", Result("late", "Succeeded")),
            Spawn(seq: 1, new[] { "s1" }, Result("early", "Failed")),
        };

        ActiveAttemptSelection.SelectActive(decisions)["s1"].Summary.ShouldBe("late");
    }

    [Fact]
    public void A_non_terminal_staging_decision_contributes_nothing()
    {
        // A Running spawn row (the re-park shape) has no durable results — a selector that read it would count
        // evidence that does not exist yet.
        var running = Spawn(seq: 2, new[] { "s1" }, Result("in-flight", "Succeeded")) with { Status = SupervisorDecisionStatus.Running };
        var decisions = new[] { Spawn(seq: 1, new[] { "s1" }, Result("settled", "Failed")), running };

        ActiveAttemptSelection.SelectActive(decisions)["s1"].Summary.ShouldBe("settled");
    }

    [Fact]
    public void A_multi_unit_spawn_joins_positionally()
    {
        var decisions = new[] { Spawn(seq: 1, new[] { "s1", "s2" }, Result("r1", "Succeeded"), Result("r2", "Failed")) };

        var active = ActiveAttemptSelection.SelectActive(decisions);

        active["s1"].Summary.ShouldBe("r1");
        active["s2"].Summary.ShouldBe("r2");
    }

    [Fact]
    public void Non_staging_decisions_and_idless_retries_are_ignored()
    {
        var decisions = new[]
        {
            Spawn(seq: 1, new[] { "s1" }, Result("kept", "Succeeded")),
            new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = 2, DecisionKind = SupervisorDecisionKinds.Merge, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}", OutcomeJson = Outcome(Result("merge-noise", "Succeeded")) },
            new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = 3, DecisionKind = SupervisorDecisionKinds.Retry, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}", OutcomeJson = Outcome(Result("idless", "Failed")) },
        };

        var active = ActiveAttemptSelection.SelectActive(decisions);

        active.Count.ShouldBe(1);
        active["s1"].Summary.ShouldBe("kept");
    }

    // ── Builders ────────────────────────────────────────────────────────────────────────────────────────────

    private static SupervisorPriorDecision Spawn(long seq, string[] subtaskIds, params SupervisorAgentResult[] results) => new()
    {
        Id = Guid.NewGuid(),
        Sequence = seq,
        DecisionKind = SupervisorDecisionKinds.Spawn,
        Status = SupervisorDecisionStatus.Succeeded,
        PayloadJson = JsonSerializer.Serialize(new { subtaskIds }),
        OutcomeJson = Outcome(results),
    };

    private static SupervisorPriorDecision Retry(long seq, string subtaskId, SupervisorAgentResult result) => new()
    {
        Id = Guid.NewGuid(),
        Sequence = seq,
        DecisionKind = SupervisorDecisionKinds.Retry,
        Status = SupervisorDecisionStatus.Succeeded,
        PayloadJson = JsonSerializer.Serialize(new { subtaskId }),
        OutcomeJson = Outcome(result),
    };

    private static string Outcome(params SupervisorAgentResult[] results) =>
        JsonSerializer.Serialize(new { agentResults = results }, CodeSpace.Core.Services.Agents.AgentJson.Options);

    private static SupervisorAgentResult Result(string summary, string status) => new()
    {
        AgentRunId = Guid.NewGuid(),
        Status = status,
        Summary = summary,
    };
}
