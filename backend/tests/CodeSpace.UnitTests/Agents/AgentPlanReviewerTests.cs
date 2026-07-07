using CodeSpace.Core.Services.Agents.Review;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// The D① grounded plan reviewer's PURE contracts: the review instructions (verify-against-the-real-tree framing, the
/// four checks, read-only), the checklist-invisible <c>#plan-review</c> iteration key, and the default-branch clone
/// (a plan targets the repo's CURRENT state — no produced branch exists yet).
/// </summary>
[Trait("Category", "Unit")]
public sealed class AgentPlanReviewerTests
{
    [Fact]
    public void The_instructions_ground_the_review_in_the_real_tree_and_embed_goal_and_plan()
    {
        var body = AgentPlanReviewer.BuildReviewInstructions("Goal: g\nSubtasks:\n  - t: i", "ship the feature");

        body.ShouldContain("VERIFY the plan against the ACTUAL code", customMessage: "the whole point of D① — a grounded review, not a text impression");
        body.ShouldContain("do its assumptions hold", customMessage: "check (1): the plan's presumed files/frameworks/test infra");
        body.ShouldContain("feasible and necessary", customMessage: "check (2)");
        body.ShouldContain("already done", customMessage: "check (3): a plan scheduling finished work is a broken plan");
        body.ShouldContain("go unplanned", customMessage: "check (4): completeness against the goal");
        body.ShouldContain("ACCEPTANCE actually SATISFIABLE", customMessage: "check (5) ⑧: whether each subtask's declared 'done' can even be verified against the tree");
        body.ShouldContain("dooms its subtask to endless retry", customMessage: "⑧: the error class that killed the forensics run — an acceptance that can never pass, retried forever, invisible to every reviewer rung until now");
        body.ShouldContain("You did not write the plan", customMessage: "the independence framing");
        body.ShouldContain("Do NOT modify anything", customMessage: "the reviewer READS — it never writes");
        body.ShouldContain("READ-ONLY and command-restricted", customMessage: "the capability context — the reviewer's sandbox is restricted BY DESIGN");
        body.ShouldContain("Never judge the plan's feasibility from your own write/exec failures", customMessage: "a real run was derailed by a reviewer inferring the EXECUTORS' environment from its own sandbox wall");
        body.ShouldContain("WRITABLE workspaces", customMessage: "the executors' actual capabilities are stated so the reviewer can't mistake its cage for theirs");
        body.ShouldContain("ship the feature", customMessage: "the goal is the reviewer's yardstick");
        body.ShouldContain("- t: i", customMessage: "the rendered plan rides verbatim");
    }

    [Fact]
    public void The_plan_review_iteration_key_is_pinned_and_checklist_safe()
    {
        AgentPlanReviewer.IterationKey.ShouldBe("#plan-review");

        // The S5 checklist join guard: the plan-review key can never be mistaken for a fan-out branch index.
        Core.Services.Plans.WorkPlanChecklistService.TryParseBranchIndex(AgentPlanReviewer.IterationKey, out _)
            .ShouldBeFalse("the positional map#i join must never adopt a plan-review run as a branch attempt");
    }

    [Fact]
    public void A_plan_review_task_clones_the_default_branch_read_only()
    {
        var repositoryId = Guid.NewGuid();

        var task = AgentReviewRunner.BuildReviewTask(new AgentReviewSpec
        {
            SubjectInstructions = "verify the plan",
            RepositoryId = repositoryId,
            BaseRef = null,   // what AgentPlanReviewer passes — a plan has no produced branch
            TeamId = Guid.NewGuid(),
            IterationKey = AgentPlanReviewer.IterationKey,
        }, "codex-cli");

        task.RepositoryId.ShouldBe(repositoryId);
        task.Workspace.ShouldBeNull("no BaseRef ⇒ no pinned-ref workspace — the executor clones the repository's DEFAULT branch, the tree the plan's first agent would see");
        task.Autonomy.ShouldBe(AgentAutonomyLevel.Confined, "the reviewer READS — it never writes");
        task.Goal.ShouldContain(AgentReviewRunner.VerdictMarker, customMessage: "the shared final-message contract rides every review goal");
    }
}
