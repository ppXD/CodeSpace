using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Review;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The S6 revise loop's PURE decision logic — the budget, the trigger, the fed-back task, and the round-scoped
/// spool key. These are the invariants that keep the loop bounded and honest: an explicit budget is clamped, only
/// agent-fixable failures buy a round (a grade-error is infra; a Gate flag is a flag), a warm resume requires BOTH
/// the session id and its transcript, and a revise round can never inherit a finished spool's exit marker.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AgentRunExecutorReviseTests
{
    // ─── EffectiveReviseRounds ────────────────────────────────────────────────

    [Theory]
    [InlineData(null, ReviewMode.None, 0)]     // no budget, no Improve → S5 hard-gate semantics unchanged
    [InlineData(null, ReviewMode.Gate, 0)]     // Gate is a flag, never a re-run
    [InlineData(null, ReviewMode.Improve, 1)]  // Improve MEANS improve — one implicit round
    [InlineData(0, ReviewMode.Improve, 0)]     // an explicit 0 wins over the Improve default (operator said no)
    [InlineData(2, ReviewMode.None, 2)]        // an explicit budget needs no critic — the oracle alone can trigger
    [InlineData(99, ReviewMode.None, 3)]       // clamped to the cap — a runaway budget can't buy runaway billing
    [InlineData(-5, ReviewMode.Improve, 0)]    // a negative explicit value clamps to 0 (and beats the Improve default)
    public void The_revise_budget_is_explicit_first_then_improve_implies_one(int? rounds, ReviewMode mode, int expected) =>
        AgentRunExecutor.EffectiveReviseRounds(TaskWith(rounds: rounds, mode: mode)).ShouldBe(expected);

    [Fact]
    public void The_rounds_cap_is_pinned() =>
        AgentRunExecutor.MaxReviseRoundsCap.ShouldBe(3);

    // ─── ReviseReasonFor ─────────────────────────────────────────────────────

    [Fact]
    public void An_oracle_failure_buys_a_round_with_the_grade_detail()
    {
        var reason = AgentRunExecutor.ReviseReasonFor(TaskWith(), AcceptanceFailed("exit 1: 2 tests failed"));

        reason.ShouldNotBeNull();
        reason.ShouldContain("exit 1: 2 tests failed", customMessage: "the agent is told exactly WHY the check failed");
    }

    [Fact]
    public void A_grade_error_is_infra_and_never_buys_a_round() =>
        AgentRunExecutor.ReviseReasonFor(TaskWith(), AcceptanceFailed("grade-error: clone exploded"))
            .ShouldBeNull("another agent round cannot fix the grader");

    [Fact]
    public void A_missing_deliverable_buys_a_round_because_producing_it_is_the_fix()
    {
        // S7: a non-coding oracle's "artifact-missing" is the rubric/citation/schema twin of "the agent did no work" —
        // the revise instruction tells it exactly which deliverable to produce.
        var missingDeliverable = AcceptanceFailed("artifact-missing: report.md");

        AgentRunExecutor.ReviseReasonFor(TaskWith(), missingDeliverable).ShouldNotBeNull();
    }

    [Fact]
    public void No_branch_with_no_work_buys_a_round_because_doing_the_work_is_the_fix()
    {
        var didNothing = AcceptanceFailed("no-branch-or-repo") with { ChangedFiles = Array.Empty<string>(), Patch = null };

        AgentRunExecutor.ReviseReasonFor(TaskWith(), didNothing).ShouldNotBeNull();
    }

    [Fact]
    public void No_branch_despite_produced_work_is_a_publish_failure_and_never_buys_a_round()
    {
        // The work exists but the branch didn't publish (credential-less clone / push infra) — another agent
        // pass can't fix the publish, so revising would burn the whole budget on an infra condition.
        var publishFailed = AcceptanceFailed("no-branch-or-repo");   // fixture carries ChangedFiles

        AgentRunExecutor.ReviseReasonFor(TaskWith(), publishFailed).ShouldBeNull();
    }

    [Fact]
    public void An_improve_flag_buys_a_round_with_the_critique()
    {
        var flagged = Flagged("missing tests for the new path");

        var reason = AgentRunExecutor.ReviseReasonFor(TaskWith(mode: ReviewMode.Improve), flagged);

        reason.ShouldNotBeNull();
        reason.ShouldContain("missing tests for the new path", customMessage: "the critique is the food");
    }

    [Fact]
    public void A_gate_flag_stays_a_flag() =>
        AgentRunExecutor.ReviseReasonFor(TaskWith(mode: ReviewMode.Gate), Flagged("weak"))
            .ShouldBeNull("only Improve buys a re-run — Gate hands straight to a human");

    [Fact]
    public void A_flag_with_no_feedback_never_buys_a_round() =>
        AgentRunExecutor.ReviseReasonFor(TaskWith(mode: ReviewMode.Improve), Flagged(feedback: null))
            .ShouldBeNull("there is nothing to feed back — a blind retry is not a revision");

    [Fact]
    public void A_clean_success_never_revises() =>
        AgentRunExecutor.ReviseReasonFor(TaskWith(mode: ReviewMode.Improve), Succeeded()).ShouldBeNull();

    [Fact]
    public void A_deferred_gate_never_revises()
    {
        // A1 / multi-repo defer: the run stays Succeeded with a NULL verdict — no revise reason may surface, so the
        // completion choke point keeps precedence over the loop.
        var deferred = Succeeded() with { AcceptancePassed = null };

        AgentRunExecutor.ReviseReasonFor(TaskWith(mode: ReviewMode.Improve), deferred).ShouldBeNull();
    }

    [Fact]
    public void An_ordinary_failure_is_not_an_acceptance_failure() =>
        AgentRunExecutor.ReviseReasonFor(TaskWith(), Succeeded() with { Status = AgentRunStatus.Failed, ExitReason = "executor-error", Error = "boom" })
            .ShouldBeNull("only the oracle's own verdict triggers the loop — an infra failure is not revisable work");

    // ─── BuildReviseTask ─────────────────────────────────────────────────────

    [Fact]
    public void A_captured_session_makes_the_revision_warm()
    {
        var result = AcceptanceFailed("exit 1") with { SessionId = "sess-1", SessionTranscript = "{\"line\":1}" };

        var revise = AgentRunExecutor.BuildReviseTask(TaskWith(), result, "the check failed");

        revise.ResumeFromSessionId.ShouldBe("sess-1", "the SAME conversation continues — context is not thrown away");
        revise.RestoredTranscript.ShouldBe("{\"line\":1}");
        revise.RestoredTranscriptArtifactId.ShouldBeNull();
        revise.Goal.ShouldStartWith(AgentRunExecutor.ReviseInstructionPrefix);
        revise.Goal.ShouldContain("the check failed");
        revise.Goal.ShouldNotContain("Original goal", customMessage: "a warm resume already holds the goal — only the delta is sent");
    }

    [Theory]
    [InlineData("sess-1", null)]   // id captured but no transcript — --resume would error on an empty config home
    [InlineData(null, "bytes")]    // transcript without an id — nothing to hand --resume
    [InlineData(null, null)]
    public void A_missing_session_half_makes_the_revision_cold(string? sessionId, string? transcript)
    {
        var result = AcceptanceFailed("exit 1") with { SessionId = sessionId, SessionTranscript = transcript };

        var revise = AgentRunExecutor.BuildReviseTask(TaskWith(), result, "the check failed");

        revise.ResumeFromSessionId.ShouldBeNull("a warm resume needs BOTH halves — half a session cold-starts");
        revise.RestoredTranscript.ShouldBeNull();
        revise.Goal.ShouldContain("Original goal", customMessage: "a fresh conversation must carry the full contract");
        revise.Goal.ShouldContain("fix the flaky test", customMessage: "the original goal is restated verbatim");
    }

    [Fact]
    public void An_ancestor_continue_resume_is_superseded_by_this_runs_own_session()
    {
        var task = TaskWith() with { ResumeFromSessionId = "ancestor", RestoredTranscript = "old", RestoredTranscriptArtifactId = Guid.NewGuid() };
        var cold = AcceptanceFailed("exit 1");   // this run captured no session

        var revise = AgentRunExecutor.BuildReviseTask(task, cold, "reason");

        revise.ResumeFromSessionId.ShouldBeNull("resuming the ANCESTOR would rewind past this run's own work");
        revise.RestoredTranscript.ShouldBeNull();
        revise.RestoredTranscriptArtifactId.ShouldBeNull("a stale offloaded ref must not be re-resolved");
    }

    [Fact]
    public void The_contract_rides_the_revise_task_unchanged()
    {
        var task = TaskWith(rounds: 2, mode: ReviewMode.Improve);

        var revise = AgentRunExecutor.BuildReviseTask(task, AcceptanceFailed("exit 1"), "reason");

        revise.Acceptance.ShouldBe(task.Acceptance, "the revision is judged by the SAME oracle");
        revise.OutputReviewMode.ShouldBe(ReviewMode.Improve);
        revise.MaxReviseRounds.ShouldBe(2);
        revise.Harness.ShouldBe(task.Harness);
        revise.RepositoryId.ShouldBe(task.RepositoryId);
    }

    [Fact]
    public void The_revise_prefix_is_pinned() =>
        AgentRunExecutor.ReviseInstructionPrefix.ShouldBe("REVISE:", "an operator-visible transcript marker + the deterministic test CLIs' hook");

    // ─── Spool key + transcript seam ─────────────────────────────────────────

    [Fact]
    public void The_first_attempt_keeps_the_bare_run_key_and_rounds_get_their_own()
    {
        var runId = Guid.NewGuid();

        AgentRunExecutor.ReviseSpoolKey(runId, 0).ShouldBe(runId.ToString("N"), "every non-revised run's spool path is byte-identical to pre-S6");
        AgentRunExecutor.ReviseSpoolKey(runId, 1).ShouldBe($"{runId:N}-r1", "a revise round must never inherit a finished spool's exit marker");
        AgentRunExecutor.ReviseSpoolKey(runId, 2).ShouldBe($"{runId:N}-r2");
    }

    [Fact]
    public void Transcripts_join_with_a_visible_seam()
    {
        AgentRunExecutor.JoinTranscripts("round0", "round1").ShouldBe("round0\n--- revise round ---\nround1");
        AgentRunExecutor.JoinTranscripts(null, "only").ShouldBe("only");
        AgentRunExecutor.JoinTranscripts("only", null).ShouldBe("only");
        AgentRunExecutor.JoinTranscripts(null, null).ShouldBeNull();
    }

    [Fact]
    public void Token_usage_sums_across_rounds_so_the_cost_plane_bills_the_whole_run()
    {
        var sum = AgentRunExecutor.SumTokenUsage(new AgentTokenUsage { InputTokens = 100, OutputTokens = 40 }, new AgentTokenUsage { InputTokens = 60, OutputTokens = 25 });

        sum!.InputTokens.ShouldBe(160);
        sum.OutputTokens.ShouldBe(65);

        AgentRunExecutor.SumTokenUsage(null, new AgentTokenUsage { InputTokens = 1, OutputTokens = 2 })!.InputTokens.ShouldBe(1);
        AgentRunExecutor.SumTokenUsage(new AgentTokenUsage { InputTokens = 3, OutputTokens = 4 }, null)!.OutputTokens.ShouldBe(4);
        AgentRunExecutor.SumTokenUsage(null, null).ShouldBeNull();
    }

    [Fact]
    public void The_review_feedback_folds_rationale_and_issues()
    {
        AgentRunExecutor.RenderReviewFeedback(new CriticVerdict { Mode = ReviewMode.Improve, Approved = false, Rationale = "incomplete", Issues = new[] { new CriticIssue { Text = "no tests" }, new CriticIssue { Text = "typo" } } })
            .ShouldBe("incomplete Issues: no tests; typo");

        AgentRunExecutor.RenderReviewFeedback(new CriticVerdict { Mode = ReviewMode.Improve, Approved = false, Rationale = "weak" })
            .ShouldBe("weak");
    }

    // ─── fixtures ────────────────────────────────────────────────────────────

    private static AgentTask TaskWith(int? rounds = null, ReviewMode mode = ReviewMode.None) => new()
    {
        Goal = "fix the flaky test",
        Harness = "codex-cli",
        RepositoryId = Guid.NewGuid(),
        Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "sh", "check.sh" } },
        MaxReviseRounds = rounds,
        OutputReviewMode = mode,
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

    private static AgentRunResult Flagged(string? feedback) => Succeeded() with
    {
        Status = AgentRunStatus.NeedsReview,
        ExitReason = "output-flagged",
        ReviewFeedback = feedback,
    };
}
