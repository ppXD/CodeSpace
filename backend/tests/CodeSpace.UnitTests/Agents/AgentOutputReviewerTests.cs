using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Review;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// The S8 agent reviewer's PURE contracts: the distinct-first harness ladder, the recursion-proof review task, the
/// VERDICT final-message parse (fail-closed to the model-critic ladder), and the checklist-safe iteration key.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AgentOutputReviewerTests
{
    // ─── The harness ladder ──────────────────────────────────────────────────

    [Fact]
    public void The_ladder_prefers_a_harness_different_from_the_producers()
    {
        var registered = new[] { Harness("codex-cli"), Harness("claude-code") };

        AgentOutputReviewer.PickReviewerHarness("codex-cli", registered).ShouldBe("claude-code", "a genuinely independent second toolchain when one is registered");
        AgentOutputReviewer.PickReviewerHarness("claude-code", registered).ShouldBe("codex-cli");
    }

    [Fact]
    public void A_one_harness_deployment_falls_back_to_the_same_harness() =>
        AgentOutputReviewer.PickReviewerHarness("codex-cli", new[] { Harness("codex-cli") })
            .ShouldBe("codex-cli", "an independent AGENT + model is still a real second opinion");

    // ─── The review task (recursion-proof by construction) ──────────────────

    [Fact]
    public void The_review_task_is_read_only_recursion_proof_and_cloned_at_the_produced_branch()
    {
        var producer = new AgentTask
        {
            Goal = "add validation",
            Harness = "codex-cli",
            RepositoryId = Guid.NewGuid(),
            OutputReviewMode = ReviewMode.Improve,
            ReviewerAgent = true,
            ReviewerModelId = Guid.NewGuid(),
            MaxReviseRounds = 2,
        };
        var result = new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", ProducedBranch = "codespace/agent/x", ChangedFiles = new[] { "src/login.cs" } };

        var review = AgentOutputReviewer.BuildReviewTask(producer, result, "claude-code");

        review.Harness.ShouldBe("claude-code");
        review.Autonomy.ShouldBe(AgentAutonomyLevel.Confined, "the reviewer READS — it never writes");
        review.Workspace!.Repositories[0].Ref.ShouldBe("codespace/agent/x", "the reviewer inspects the PRODUCED tree, not the default branch");
        review.OutputReviewMode.ShouldBe(ReviewMode.None, "a reviewer never gets reviewed");
        review.ReviewerAgent.ShouldBeFalse("a reviewer never spawns a reviewer");
        review.MaxReviseRounds.ShouldBe(0, "a reviewer never self-revises");
        review.Acceptance.ShouldBeNull("a reviewer carries no oracle of its own");
        review.PushProducedBranch.ShouldBeNull("nothing to publish");
        review.ModelCredentialModelId.ShouldBe(producer.ReviewerModelId, "the operator's reviewer model pin drives the reviewer agent's model");
        review.TimeoutSeconds.ShouldBe(AgentOutputReviewer.ReviewerTimeoutSeconds);
        review.Goal.ShouldContain(AgentOutputReviewer.VerdictMarker, customMessage: "the goal quotes the exact final-message contract");
        review.Goal.ShouldContain("add validation", customMessage: "the producer's goal is the reviewer's yardstick");
        review.Goal.ShouldContain("src/login.cs");
    }

    [Fact]
    public void The_review_iteration_key_is_checklist_safe()
    {
        AgentOutputReviewer.ReviewIterationKey("map#0").ShouldBe("map#0#review");
        AgentOutputReviewer.ReviewIterationKey("").ShouldBe("#review");
        AgentOutputReviewer.ReviewIterationKey(null).ShouldBe("#review");

        // The S5 checklist join guard: a review key can never be mistaken for a fan-out branch index.
        Core.Services.Plans.WorkPlanChecklistService.TryParseBranchIndex("map#0#review", out _)
            .ShouldBeFalse("the positional map#i join must never adopt a reviewer run as a branch attempt");
    }

    // ─── The VERDICT parse (fail-closed → the model-critic ladder) ───────────

    [Fact]
    public void A_contract_final_message_parses_into_an_evidence_attached_verdict()
    {
        var verdict = AgentOutputReviewer.ParseVerdict(
            """Reviewed. VERDICT: {"approved": false, "rationale": "placeholder hack", "issues": [{"issue": "hack committed", "evidence": "feature.txt line 1"}]}""");

        verdict.Failed.ShouldBeFalse();
        verdict.Approved.ShouldBeFalse();
        verdict.Rationale.ShouldBe("placeholder hack");
        verdict.Issues.ShouldContain(i => i.Text == "hack committed" && i.Evidence == "feature.txt line 1");
    }

    [Fact]
    public void The_LAST_marker_wins_so_quoted_contract_text_in_prose_never_shadows_the_verdict()
    {
        var verdict = AgentOutputReviewer.ParseVerdict(
            """The goal said to end with VERDICT: {...} — here it is. VERDICT: {"approved": true, "rationale": "clean"}""");

        verdict.Failed.ShouldBeFalse();
        verdict.Approved.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("I looked around and it seems fine.")]                          // no marker
    [InlineData("VERDICT: not json at all")]                                    // unparseable
    [InlineData("""VERDICT: {"approved": true}""")]                             // no rationale — not auditable
    public void A_broken_final_message_fails_closed_to_the_ladder(string? finalMessage) =>
        AgentOutputReviewer.ParseVerdict(finalMessage).Failed
            .ShouldBeTrue("a review that can't state its verdict in-contract is not a verdict — the executor ladders down to the model critic");

    [Fact]
    public void The_verdict_marker_is_pinned() =>
        AgentOutputReviewer.VerdictMarker.ShouldBe("VERDICT:");

    private static Core.Services.Agents.IAgentHarness Harness(string kind) => new FakeHarness(kind);

    private sealed class FakeHarness : Core.Services.Agents.IAgentHarness
    {
        public FakeHarness(string kind) => Kind = kind;
        public string Kind { get; }
        public string Version => "test";
        public IReadOnlyList<string> Models { get; } = Array.Empty<string>();
        public Messages.Agents.SandboxSpec BuildInvocation(AgentTask task) => throw new NotSupportedException();
        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine) => throw new NotSupportedException();
        public AgentRunResult BuildResult(IReadOnlyList<AgentEvent> events, int exitCode) => throw new NotSupportedException();
    }
}
