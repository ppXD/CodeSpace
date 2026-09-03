using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// D3 — the QUICK lane's model escalation: the two pure halves the executor's revise loop composes. (1) The
/// per-round trigger projected off an <see cref="AgentRunResult"/> — the round's own evidence that the MODEL was
/// insufficient, never a grader/environment/gateway fault. (2) Applying the pick to the next round's task, including
/// the one-model case where the pool holds nothing stronger and the round must run on the SAME model with the fact
/// recorded rather than vanishing.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AgentRunExecutorEscalationTests
{
    [Fact]
    public void An_over_claiming_round_asks_for_a_stronger_model()
    {
        var reason = AgentRunExecutor.EscalationReasonFor(AcceptanceFailed("tests-failed-exit-1") with { Contradiction = AgentContradiction.OverClaim });

        reason.ShouldNotBeNull();
        reason!.ShouldContain("claimed success");
    }

    [Fact]
    public void A_failed_check_with_work_present_asks_for_a_stronger_model() =>
        AgentRunExecutor.EscalationReasonFor(AcceptanceFailed("tests-failed-exit-1")).ShouldNotBeNull("the agent DID produce work and the check still failed — that is evidence about the model");

    [Fact]
    public void An_infra_classed_failure_never_asks_for_a_stronger_model() =>
        AgentRunExecutor.EscalationReasonFor(AcceptanceFailed("grade-error: clone exploded") with { Contradiction = AgentContradiction.OverClaim })
            .ShouldBeNull("the grader itself failed — escalating spends a pricier tier on a verdict no model can move");

    [Fact]
    public void A_gateway_format_fault_never_asks_for_a_stronger_model() =>
        AgentRunExecutor.EscalationReasonFor(AcceptanceFailed("tests-failed-exit-1") with { Error = "400 messages.1.content.0.type: is not a thinking block" })
            .ShouldBeNull("the cause-aware retry owns this one — the same gateway would mangle a pricier model's wire identically");

    [Fact]
    public void A_critic_flagged_round_never_asks_for_a_stronger_model() =>
        AgentRunExecutor.EscalationReasonFor(Flagged()).ShouldBeNull("a style critique is not evidence the model was too weak to do the work");

    [Fact]
    public void A_passing_round_never_asks_for_a_stronger_model() =>
        AgentRunExecutor.EscalationReasonFor(Succeeded()).ShouldBeNull();

    // ─── applying the pick ────────────────────────────────────────────────────

    [Fact]
    public void An_escalated_pick_overrides_the_tasks_pinned_model()
    {
        // The supervisor lane's precedent (#1061): an operator's ordinary model choice is a FLOOR for untested work,
        // not a ceiling once the run's own check has disproved it.
        var task = TaskWith("claude-haiku-4-5");

        var escalated = AgentRunExecutor.ApplyEscalation(task, new AgentModelEscalation { From = "claude-haiku-4-5", To = "claude-sonnet-4-5", Reason = "r" });

        escalated.Model.ShouldBe("claude-sonnet-4-5");
    }

    [Fact]
    public void A_no_stronger_model_outcome_leaves_the_model_exactly_as_it_was()
    {
        var task = TaskWith("claude-haiku-4-5");

        var same = AgentRunExecutor.ApplyEscalation(task, new AgentModelEscalation { From = "claude-haiku-4-5", To = null, Reason = "r" });

        same.Model.ShouldBe("claude-haiku-4-5", "a one-model team keeps its only model — the FACT is recorded elsewhere, the dispatch is untouched");
        same.ShouldBe(task, "byte-identical task — a no-op escalation must not perturb the dispatch at all");
    }

    [Fact]
    public void No_escalation_at_all_leaves_the_task_untouched() =>
        AgentRunExecutor.ApplyEscalation(TaskWith("claude-haiku-4-5"), null).Model.ShouldBe("claude-haiku-4-5");

    [Fact]
    public void The_escalation_note_names_the_move_and_the_no_op_distinctly()
    {
        AgentRunExecutor.DescribeEscalation(new AgentModelEscalation { From = "a", To = "b", Reason = "the check failed" })
            .ShouldBe($"{AgentRunExecutor.ModelEscalationPrefix}: a → b. the check failed");

        AgentRunExecutor.DescribeEscalation(new AgentModelEscalation { From = "a", To = null, Reason = "the check failed" })
            .ShouldBe($"{AgentRunExecutor.ModelEscalationPrefix}: no model stronger than a is credentialed for this team — staying on a. the check failed");
    }

    private static AgentTask TaskWith(string model) => new()
    {
        Goal = "do the thing",
        Harness = "claude-code",
        Model = model,
        Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "sh", "check.sh" } },
    };

    private static AgentRunResult Succeeded() => new()
    {
        Status = AgentRunStatus.Succeeded,
        ExitReason = "completed",
        ProducedBranch = "codespace/agent/x",
        ChangedFiles = new[] { "a.cs" },
        AcceptancePassed = true,
    };

    private static AgentRunResult AcceptanceFailed(string detail) => Succeeded() with
    {
        Status = AgentRunStatus.Failed,
        ExitReason = "acceptance-failed",
        AcceptancePassed = false,
        AcceptanceDetail = detail,
    };

    private static AgentRunResult Flagged() => Succeeded() with
    {
        Status = AgentRunStatus.NeedsReview,
        ExitReason = "output-flagged",
        ReviewFeedback = "nit: naming",
    };
}
