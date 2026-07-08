using CodeSpace.Core.Services.Agents.Review;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Review;
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

        AgentReviewRunner.PickReviewerHarness("codex-cli", registered).ShouldBe("claude-code", "a genuinely independent second toolchain when one is registered");
        AgentReviewRunner.PickReviewerHarness("claude-code", registered).ShouldBe("codex-cli");
    }

    [Fact]
    public void A_one_harness_deployment_falls_back_to_the_same_harness() =>
        AgentReviewRunner.PickReviewerHarness("codex-cli", new[] { Harness("codex-cli") })
            .ShouldBe("codex-cli", "an independent AGENT + model is still a real second opinion");

    [Fact]
    public void A_model_produced_artifact_takes_the_first_registered_harness() =>
        AgentReviewRunner.PickReviewerHarness("", new[] { Harness("codex-cli"), Harness("claude-code") })
            .ShouldBe("codex-cli", "a plan's producer is a MODEL, not a harness — any registered harness is an independent agent (D①)");

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

        var review = AgentReviewRunner.BuildReviewTask(new AgentReviewSpec
        {
            SubjectInstructions = AgentOutputReviewer.BuildReviewInstructions(producer.Goal, result),
            RepositoryId = producer.RepositoryId!.Value,
            BaseRef = result.ProducedBranch,
            TeamId = Guid.NewGuid(),
            IterationKey = "#review",
            ProducerHarness = producer.Harness,
            ReviewerModelId = producer.ReviewerModelId,
        }, "claude-code");

        review.Harness.ShouldBe("claude-code");
        review.Autonomy.ShouldBe(AgentAutonomyLevel.Confined, "the reviewer READS — it never writes");
        review.Workspace!.Repositories[0].Ref.ShouldBe("codespace/agent/x", "the reviewer inspects the PRODUCED tree, not the default branch");
        review.OutputReviewMode.ShouldBe(ReviewMode.None, "a reviewer never gets reviewed");
        review.ReviewerAgent.ShouldBeFalse("a reviewer never spawns a reviewer");
        review.MaxReviseRounds.ShouldBe(0, "a reviewer never self-revises");
        review.Acceptance.ShouldBeNull("a reviewer carries no oracle of its own");
        review.PushProducedBranch.ShouldBe(false, "a reviewer never publishes — explicit opt-out, not deferred to the (now default-on) push behavior");
        review.ModelCredentialModelId.ShouldBe(producer.ReviewerModelId, "the operator's reviewer model pin drives the reviewer agent's model");
        review.TimeoutSeconds.ShouldBe(AgentReviewRunner.ReviewerTimeoutSeconds);
        review.Goal.ShouldContain(AgentReviewRunner.VerdictMarker, customMessage: "the goal quotes the exact final-message contract");
        review.Goal.ShouldContain("add validation", customMessage: "the producer's goal is the reviewer's yardstick");
        review.Goal.ShouldContain("src/login.cs");
        review.Goal.ShouldContain("READ-ONLY and command-restricted", customMessage: "the capability context — the reviewer must never infer the producer's environment from its own sandbox wall");
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
    public void A_contract_final_message_parses_into_an_evidence_and_severity_attached_verdict()
    {
        var verdict = AgentReviewRunner.ParseVerdict(
            """Reviewed. VERDICT: {"approved": false, "rationale": "placeholder hack", "issues": [{"issue": "hack committed", "evidence": "feature.txt line 1", "severity": "blocker"}]}""");

        verdict.Failed.ShouldBeFalse();
        verdict.Approved.ShouldBeFalse("the agent named a Blocker — the Gate halts (severity-authoritative, uniform with the in-process critic)");
        verdict.Rationale.ShouldBe("placeholder hack");
        verdict.Issues.ShouldContain(i => i.Text == "hack committed" && i.Evidence == "feature.txt line 1" && i.Severity == CriticSeverity.Blocker);
    }

    [Fact]
    public void The_agent_gate_is_severity_authoritative_so_a_major_only_disapproval_no_longer_halts()
    {
        // P1: the agent reviewer's Gate uses the SAME rule as the in-process critic — halt iff a Blocker. A
        // Major/Minor-only flag (even with the model's raw approved:false) does not halt, so a sound produced change
        // with a non-fatal concern is not blocked into a NeedsReview.
        AgentReviewRunner.ParseVerdict(
            """VERDICT: {"approved": false, "rationale": "a naming nit", "issues": [{"issue": "terse name", "evidence": "x at line 3", "severity": "minor"}]}""")
            .Approved.ShouldBeTrue("a Minor-only disapproval no longer halts the agent gate — the calibration fix");
    }

    [Fact]
    public void The_LAST_marker_wins_so_quoted_contract_text_in_prose_never_shadows_the_verdict()
    {
        var verdict = AgentReviewRunner.ParseVerdict(
            """The goal said to end with VERDICT: {...} — here it is. VERDICT: {"approved": true, "rationale": "clean"}""");

        verdict.Failed.ShouldBeFalse();
        verdict.Approved.ShouldBeTrue("no issue ⇒ no blocker ⇒ approved");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("I looked around and it seems fine.")]                          // no marker
    [InlineData("VERDICT: not json at all")]                                    // unparseable
    [InlineData("""VERDICT: {"approved": true}""")]                             // no rationale — not auditable
    public void A_broken_final_message_fails_closed_to_the_ladder(string? finalMessage) =>
        AgentReviewRunner.ParseVerdict(finalMessage).Failed
            .ShouldBeTrue("a review that can't state its verdict in-contract is not a verdict — the executor ladders down to the model critic");

    [Fact]
    public void The_verdict_marker_is_pinned() =>
        AgentReviewRunner.VerdictMarker.ShouldBe("VERDICT:");

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
