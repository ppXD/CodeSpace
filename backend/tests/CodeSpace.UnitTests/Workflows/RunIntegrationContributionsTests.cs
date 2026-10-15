using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Enums;
using Shouldly;
using System.Text.Json;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The run-sourced contribution mapping behind <c>git.integrate_run</c>: which of a run's produced work
/// integrates, in what order, carrying which patch bytes. Pins: only THIS repository's Agent-kind pushed/
/// patch-only manifests qualify (None-state and foreign-repo rows never leak in); order is agent-run creation
/// (deterministic apply order); an offloaded patch rides its artifact id while a SMALL patch falls back to the
/// result row's inline bytes (single-repo top-level, multi-repo per-alias) — without that fallback every
/// sub-threshold diff would read patch-less and block a clean integration; the label is the completion
/// protocol's own unit id, so the integration outcome names the same units the contract ledger stakes.
///
/// <para>And ONE attempt per unit: a retried subtask respawns a fresh agent run whose manifest row lands beside
/// the abandoned attempt's, so without the reduction the unit conflicted with ITSELF and parked the run. Pinned
/// as a reduction over superseded DUPLICATES and not an outcome filter — a lone failed-but-captured attempt still
/// contributes, and a fan-out's distinct units all survive.</para>
///
/// <para>The ONE verdict that DOES withhold work: a unit whose own definition-of-done rejected it (or whose
/// verification a human waived) never reaches the reviewable candidate — the ledger-row analogue of the deep
/// lane's <c>SupervisorOutcome.IsWithheldFromHead</c>. Pinned as a theory over all four
/// <c>PublishAcceptanceState</c> values, plus that withholding one unit never withholds its siblings.</para>
///
/// <para>Pinned with it, the LANE FENCE that keeps that reduction honest: a supervisor stamps one turn cell on all
/// K agents it spawns in a turn, so a supervisor-staked row is never reduced against a cell-sharing peer — K
/// concurrent deliverables would otherwise collapse to one and silently lose K-1. An unreadable task envelope
/// takes the same conservative branch. And the invariant's real scope: one attempt per (unit, REPOSITORY).</para>
/// </summary>
[Trait("Category", "Unit")]
public class RunIntegrationContributionsTests
{
    private static readonly Guid Repo = Guid.NewGuid();

    [Fact]
    public void Only_this_repositories_produced_manifests_qualify()
    {
        var mine = Guid.NewGuid();
        var foreignRepo = Guid.NewGuid();
        var noneState = Guid.NewGuid();

        var contributions = RunIntegrationContributions.Build(Repo,
            new[]
            {
                Manifest(mine, Repo, PublishState.Pushed, branch: "codespace/agent/a"),
                Manifest(Guid.NewGuid(), foreignRepo, PublishState.Pushed, branch: "codespace/agent/b"),
                Manifest(noneState, Repo, PublishState.None),
            },
            new[] { Work(mine, "agent", "map#0", 1), Work(noneState, "agent", "map#1", 2) });

        contributions.ShouldHaveSingleItem().ProducedBranch.ShouldBe("codespace/agent/a");
    }

    [Fact]
    public void Contributions_apply_in_agent_run_creation_order()
    {
        var late = Guid.NewGuid();
        var early = Guid.NewGuid();

        var contributions = RunIntegrationContributions.Build(Repo,
            new[] { Manifest(late, Repo, PublishState.Pushed), Manifest(early, Repo, PublishState.Pushed) },
            new[] { Work(late, "agent", "map#1", minute: 9), Work(early, "agent", "map#0", minute: 3) });

        contributions.Select(c => c.Label).ShouldBe(new[] { "agent#map#0", "agent#map#1" });
    }

    [Fact]
    public void An_offloaded_patch_rides_its_artifact_id()
    {
        var runId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();

        var contribution = RunIntegrationContributions.Build(Repo,
            new[] { Manifest(runId, Repo, PublishState.PatchOnly, patchArtifactId: artifactId) },
            new[] { Work(runId, "agent", "map#0", 1) }).ShouldHaveSingleItem();

        contribution.PatchArtifactId.ShouldBe(artifactId);
        contribution.Patch.ShouldBe("", customMessage: "the integrator resolves the artifact itself — this layer never double-loads");
    }

    [Fact]
    public void A_small_patch_falls_back_to_the_results_inline_bytes()
    {
        var runId = Guid.NewGuid();
        var resultJson = JsonSerializer.Serialize(new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Patch = "diff --git a/x b/x" }, AgentJson.Options);

        var contribution = RunIntegrationContributions.Build(Repo,
            new[] { Manifest(runId, Repo, PublishState.Pushed) },
            new[] { Work(runId, "agent", "map#0", 1, resultJson) }).ShouldHaveSingleItem();

        contribution.Patch.ShouldBe("diff --git a/x b/x");
    }

    [Fact]
    public void A_multi_repo_result_matches_the_inline_patch_by_alias()
    {
        var runId = Guid.NewGuid();
        var resultJson = JsonSerializer.Serialize(new AgentRunResult
        {
            Status = AgentRunStatus.Succeeded,
            ExitReason = "completed",
            Patch = "diff-primary-compatibility-mirror",
            RepositoryResults = new[]
            {
                new RepositoryRunResult { Alias = "primary", Patch = "diff-primary" },
                new RepositoryRunResult { Alias = "web", Patch = "diff-web" },
            },
        }, AgentJson.Options);

        var contribution = RunIntegrationContributions.Build(Repo,
            new[] { Manifest(runId, Repo, PublishState.Pushed, alias: "web") },
            new[] { Work(runId, "agent", "map#0", 1, resultJson) }).ShouldHaveSingleItem();

        contribution.Patch.ShouldBe("diff-web", customMessage: "git.integrate_run must bind the manifest alias before the multi-repo result's primary compatibility mirror");
    }

    [Fact]
    public void An_unparseable_result_contributes_empty_bytes_never_a_guess()
    {
        var runId = Guid.NewGuid();

        var contribution = RunIntegrationContributions.Build(Repo,
            new[] { Manifest(runId, Repo, PublishState.Pushed, branch: "codespace/agent/x") },
            new[] { Work(runId, "agent", "map#0", 1, "{not-json") }).ShouldHaveSingleItem();

        contribution.Patch.ShouldBe("", customMessage: "the integrator names the contribution unintegrable — this layer never fabricates bytes");
        contribution.ProducedBranch.ShouldBe("codespace/agent/x");
    }

    [Fact]
    public void A_manifest_with_no_agent_row_is_dropped()
    {
        RunIntegrationContributions.Build(Repo,
            new[] { Manifest(Guid.NewGuid(), Repo, PublishState.Pushed) },
            Array.Empty<RunAgentWork>()).ShouldBeEmpty();
    }

    // ─── One attempt per unit: a retry must not integrate against its own abandoned attempt ──

    [Fact]
    public void A_retried_unit_contributes_only_its_latest_attempt()
    {
        var attempt1 = Guid.NewGuid();
        var attempt2 = Guid.NewGuid();

        var contribution = RunIntegrationContributions.Build(Repo,
            new[] { Manifest(attempt1, Repo, PublishState.PatchOnly), Manifest(attempt2, Repo, PublishState.Pushed, branch: "codespace/agent/a2") },
            new[] { Work(attempt1, "agent", "map#0", minute: 1, Patch("half-done")), Work(attempt2, "agent", "map#0", minute: 7, Patch("finished")) })
            .ShouldHaveSingleItem(customMessage: "a respawned attempt SUPERSEDES the abandoned one — contributing both makes the unit conflict with itself and parks the run");

        contribution.Patch.ShouldBe("finished");
        contribution.ProducedBranch.ShouldBe("codespace/agent/a2");
    }

    [Fact]
    public void Distinct_units_each_keep_their_own_contribution()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var contributions = RunIntegrationContributions.Build(Repo,
            new[] { Manifest(first, Repo, PublishState.Pushed), Manifest(second, Repo, PublishState.Pushed) },
            new[] { Work(first, "agent", "map#0", minute: 1), Work(second, "agent", "map#1", minute: 2) });

        contributions.Select(c => c.Label).ShouldBe(new[] { "agent#map#0", "agent#map#1" },
            customMessage: "the reduction keys on the UNIT — a fan-out's siblings are different units and must all survive it");
    }

    [Fact]
    public void A_lone_failed_attempt_that_captured_a_diff_still_contributes()
    {
        var runId = Guid.NewGuid();
        var resultJson = JsonSerializer.Serialize(new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "timeout", Patch = "partial" }, AgentJson.Options);

        var contribution = RunIntegrationContributions.Build(Repo,
            new[] { Manifest(runId, Repo, PublishState.PatchOnly) },
            new[] { Work(runId, "agent", "map#0", minute: 1, resultJson) })
            .ShouldHaveSingleItem(customMessage: "superseded duplicates are removed — a unit's only attempt is never filtered by how it ended");

        contribution.Patch.ShouldBe("partial");
    }

    /// <summary>
    /// The head invariant, per manifest row: a unit its OWN definition-of-done rejected (or whose verification a
    /// human waived) never reaches the reviewable candidate — the ledger-row analogue of the deep lane's
    /// <c>SupervisorOutcome.IsWithheldFromHead</c>, which every door to the supervisor's head already enforces.
    ///
    /// <para>The map lane could not previously reach this question: its <c>flow.map</c> ran terminate-on-error, so a
    /// flunked unit failed the map and the integrate step never ran. Now the map continues past a failure, so the
    /// FIRST thing the newly-reachable path would otherwise do is put oracle-rejected work onto the one branch a
    /// human reviews. UNGRADED work (NotApplicable — no per-item contract, the dominant case) is not withheld, and
    /// <see cref="A_lone_failed_attempt_that_captured_a_diff_still_contributes"/> keeps pinning that how an attempt
    /// ENDED is still not a filter — only the verdict is.</para>
    /// </summary>
    [Theory]
    [InlineData(PublishAcceptanceState.Passed, true)]
    [InlineData(PublishAcceptanceState.NotApplicable, true)]
    [InlineData(PublishAcceptanceState.Failed, false)]
    [InlineData(PublishAcceptanceState.Waived, false)]
    public void A_unit_whose_definition_of_done_rejected_it_is_withheld_from_the_candidate(PublishAcceptanceState state, bool contributes)
    {
        var runId = Guid.NewGuid();

        var contributions = RunIntegrationContributions.Build(Repo,
            new[] { Manifest(runId, Repo, PublishState.Pushed, branch: "codespace/agent/a", acceptance: state) },
            new[] { Work(runId, "agent", "map#0", minute: 1) });

        contributions.Count.ShouldBe(contributes ? 1 : 0,
            customMessage: $"AcceptanceState={state} must {(contributes ? "reach" : "be withheld from")} the integrated candidate — the same rule SupervisorOutcome.IsWithheldFromHead applies at every door to the deep lane's head");
    }

    /// <summary>
    /// The OUTPUT review is the other half of the head invariant, and the manifest row cannot carry it: the executor
    /// upserts the row BEFORE the review runs, so a branch the reviewer flagged — or one its configured review never
    /// examined, including a run that left a decision open or ended Failed with its patch — still reads Pushed with an
    /// acceptance verdict of its own (PROBE_R2/R3: the supervisor lane withheld these units while this door integrated
    /// them). The verdict rides the run's result, read through the SAME predicate the supervisor's doors use.
    /// </summary>
    [Theory]
    [InlineData("approved", true)]
    [InlineData("no-review", true)]
    [InlineData("output-flagged", false)]
    [InlineData("output-unreviewed", false)]
    [InlineData("needs-decision", false)]
    [InlineData("failed-with-patch", false)]
    [InlineData("flagged-before-the-state-existed", false)]
    public void A_unit_its_output_review_did_not_approve_is_withheld_from_the_candidate(string shape, bool contributes)
    {
        var runId = Guid.NewGuid();
        var produced = new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", ChangedFiles = new[] { "src/Auth.cs" }, ProducedBranch = "codespace/agent/a", BaseSha = "base1" };
        var result = shape switch
        {
            "approved" => produced with { OutputReview = OutputReviewState.Approved },
            "no-review" => produced,
            "output-flagged" => produced with { Status = AgentRunStatus.NeedsReview, ExitReason = "output-flagged", ReviewFeedback = "hard-coded admin backdoor", OutputReview = OutputReviewState.Flagged },
            "output-unreviewed" => AgentOutputReviewHold.Held(produced, "812345 characters were never shown to the reviewer"),
            "needs-decision" => AgentCompletionContract.ApplyPendingDecision(AgentOutputReviewHold.Unreviewed(produced, AgentRunExecutor.DecisionOpenUnreviewedReason), Guid.NewGuid()),
            "failed-with-patch" => AgentOutputReviewHold.Unreviewed(produced with { Status = AgentRunStatus.Failed, ExitReason = "error_max_turns" }, "ended Failed before its review"),
            _ => produced with { Status = AgentRunStatus.NeedsReview, ExitReason = "output-flagged", ReviewFeedback = "hard-coded admin backdoor" },
        };

        var contributions = RunIntegrationContributions.Build(Repo,
            new[] { Manifest(runId, Repo, PublishState.Pushed, branch: "codespace/agent/a", acceptance: PublishAcceptanceState.Passed) },
            new[] { Work(runId, "agent", "map#0", minute: 1, JsonSerializer.Serialize(result, AgentJson.Options)) });

        contributions.Count.ShouldBe(contributes ? 1 : 0, $"'{shape}' must {(contributes ? "reach" : "be withheld from")} the integrated candidate — the same answer SupervisorOutcome.IsWithheldFromHead gives the deep lane");
    }

    /// <summary>A run abandoned after its push wrote no result at all. Under a configured review there is no verdict to read, so its pushed branch is withheld; with no review configured it integrates as it always has.</summary>
    [Theory]
    [InlineData(ReviewMode.Gate, false)]
    [InlineData(ReviewMode.None, true)]
    public void A_unit_with_no_result_is_withheld_only_when_its_task_configured_a_review(ReviewMode mode, bool contributes)
    {
        var runId = Guid.NewGuid();
        var task = JsonSerializer.Serialize(new AgentTask { Goal = "do the work", Harness = "codex-cli", OutputReviewMode = mode }, AgentJson.Options);

        var contributions = RunIntegrationContributions.Build(Repo,
            new[] { Manifest(runId, Repo, PublishState.Pushed, branch: "codespace/agent/a") },
            new[] { new RunAgentWork(runId, "agent", "map#0", At(1), ResultJson: null, task) });

        contributions.Count.ShouldBe(contributes ? 1 : 0);
    }

    /// <summary>
    /// Under a configured review only an approval lets a pushed unit through, and a result that says nothing is no
    /// approval. The executor writes the manifest before the review runs, so a fault anywhere after the push — the
    /// review's own ledger read, a revise round's harness, the transcript attach — replaces the result with a fresh
    /// Failed one that carries no review state, while the row still names the pushed (possibly flagged) branch. The one
    /// stateless result that does pass is the Succeeded shape an approving review wrote before the state existed.
    /// </summary>
    [Theory]
    [InlineData("approved", true)]
    [InlineData("approved-before-the-state-existed", true)]
    [InlineData("executor-fault", false)]
    public void Under_a_configured_review_a_result_that_says_nothing_is_no_approval(string shape, bool contributes)
    {
        var runId = Guid.NewGuid();
        var result = shape switch
        {
            "approved" => new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", ChangedFiles = new[] { "src/Auth.cs" }, ProducedBranch = "codespace/agent/a", OutputReview = OutputReviewState.Approved },
            "approved-before-the-state-existed" => new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", ChangedFiles = new[] { "src/Auth.cs" }, ProducedBranch = "codespace/agent/a" },
            _ => new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = AgentRunExecutor.GenericExecutorExitReason, Error = "the ledger read faulted" },
        };
        var task = JsonSerializer.Serialize(new AgentTask { Goal = "do the work", Harness = "codex-cli", OutputReviewMode = ReviewMode.Gate }, AgentJson.Options);

        var contributions = RunIntegrationContributions.Build(Repo,
            new[] { Manifest(runId, Repo, PublishState.Pushed, branch: "codespace/agent/a", acceptance: PublishAcceptanceState.Passed) },
            new[] { new RunAgentWork(runId, "agent", "map#0", At(1), JsonSerializer.Serialize(result, AgentJson.Options), task) });

        contributions.Count.ShouldBe(contributes ? 1 : 0, $"'{shape}' under a configured Gate review must {(contributes ? "reach" : "be withheld from")} the integrated candidate");
    }

    /// <summary>A flunked unit is withheld WITHOUT taking its siblings with it: the surviving sibling still integrates, which is the whole point of the map running continue-on-error rather than terminating.</summary>
    [Fact]
    public void A_withheld_unit_does_not_withhold_its_siblings()
    {
        var flunked = Guid.NewGuid();
        var passed = Guid.NewGuid();

        var contributions = RunIntegrationContributions.Build(Repo,
            new[]
            {
                Manifest(flunked, Repo, PublishState.Pushed, branch: "codespace/agent/flunked", acceptance: PublishAcceptanceState.Failed),
                Manifest(passed, Repo, PublishState.Pushed, branch: "codespace/agent/passed", acceptance: PublishAcceptanceState.Passed),
            },
            new[] { Work(flunked, "agent", "map#0", minute: 1), Work(passed, "agent", "map#1", minute: 2) });

        contributions.ShouldHaveSingleItem().ProducedBranch.ShouldBe("codespace/agent/passed");
    }

    // ─── What the head withheld is NAMED, not just dropped ───────────────────────────
    //
    // The withhold gate above removes a unit from every outcome the integration reports, so a reader of the outcome —
    // the plan-map synth first — could not tell a run that integrated everything from one that integrated everything
    // except the unit its own definition-of-done rejected, or its output review did not approve. `Withheld` is the gate's
    // other face: the same verdicts, the same rows, reported instead of discarded.

    [Fact]
    public void A_unit_whose_acceptance_failed_is_named_with_that_verdict()
    {
        var runId = Guid.NewGuid();

        var withheld = RunIntegrationContributions.Withheld(Repo,
            new[] { Manifest(runId, Repo, PublishState.Pushed, branch: "codespace/agent/a", acceptance: PublishAcceptanceState.Failed) },
            new[] { Work(runId, "agent", "map#0", minute: 1) });

        withheld.ShouldHaveSingleItem().ShouldBe(new WithheldContribution("agent#map#0", "acceptance Failed"),
            customMessage: "the label is the unit id the integration outcome names its contributions by — so a reader can line the two up");
    }

    /// <summary><c>Waived</c> is only ever stamped by the SUPERVISOR's amend-acceptance path, so this fixture is supervisor-shaped: a turn cell, an agent stamped with its own <c>WorkUnit</c>. The words say who waived it and that it was not delivered.</summary>
    [Fact]
    public void A_unit_the_supervisor_waived_is_named_as_waived_by_the_supervisor_and_not_delivered()
    {
        var runId = Guid.NewGuid();

        var withheld = RunIntegrationContributions.Withheld(Repo,
            new[] { Manifest(runId, Repo, PublishState.Pushed, branch: "codespace/agent/s1", acceptance: PublishAcceptanceState.Waived) },
            new[] { PlannedSupervisorWork(runId, "sup#turn1", "subtask-a", minute: 1) });

        withheld.ShouldHaveSingleItem().ShouldBe(new WithheldContribution("sup#sup#turn1", "acceptance waived by the supervisor (not delivered)"));
    }

    /// <summary>
    /// A unit the acceptance grade let through can still be withheld by its OUTPUT review — the verdict rides the run's
    /// result, because the executor upserts the manifest before the review runs — and the head gate withholds it through
    /// the predicate the supervisor's doors read. It must be NAMED, or it is the failure this account exists to remove: a
    /// unit dropped from the candidate and mentioned nowhere. The reason is FIXED words — the critique and the unreviewed
    /// reason are model-authored text derived from the agent's own output, so they stay on the result and never ride into
    /// the synth's prompt. Flagged (the critic objected) is told apart from did-not-approve (the review never reached a
    /// verdict over the whole result), because the account must not claim an objection nobody made.
    /// </summary>
    [Theory]
    [InlineData("output-flagged", "output review flagged it")]
    [InlineData("flagged-before-the-state-existed", "output review flagged it")]
    [InlineData("output-unreviewed", "output review did not approve it")]
    [InlineData("needs-decision", "output review did not approve it")]
    [InlineData("failed-with-patch", "output review did not approve it")]
    public void A_unit_its_output_review_did_not_approve_is_named_with_the_reviews_verdict_in_fixed_words(string shape, string reason)
    {
        var runId = Guid.NewGuid();
        var produced = new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", ChangedFiles = new[] { "src/Auth.cs" }, ProducedBranch = "codespace/agent/a", BaseSha = "base1" };
        var result = shape switch
        {
            "output-flagged" => produced with { Status = AgentRunStatus.NeedsReview, ExitReason = "output-flagged", ReviewFeedback = "hard-coded admin backdoor", OutputReview = OutputReviewState.Flagged },
            "output-unreviewed" => AgentOutputReviewHold.Held(produced, "812345 characters were never shown to the reviewer"),
            "needs-decision" => AgentCompletionContract.ApplyPendingDecision(AgentOutputReviewHold.Unreviewed(produced, AgentRunExecutor.DecisionOpenUnreviewedReason), Guid.NewGuid()),
            "failed-with-patch" => AgentOutputReviewHold.Unreviewed(produced with { Status = AgentRunStatus.Failed, ExitReason = "error_max_turns" }, "ended Failed before its review"),
            _ => produced with { Status = AgentRunStatus.NeedsReview, ExitReason = "output-flagged", ReviewFeedback = "hard-coded admin backdoor" },
        };
        var manifests = new[] { Manifest(runId, Repo, PublishState.Pushed, branch: "codespace/agent/a", acceptance: PublishAcceptanceState.Passed) };
        var work = new[] { Work(runId, "agent", "map#1", minute: 1, JsonSerializer.Serialize(result, AgentJson.Options)) };

        RunIntegrationContributions.Build(Repo, manifests, work).ShouldBeEmpty("fixture check: the output review withheld this unit from the candidate");

        var withheld = RunIntegrationContributions.Withheld(Repo, manifests, work).ShouldHaveSingleItem();

        withheld.ShouldBe(new WithheldContribution("agent#map#1", reason));
        withheld.Reason.ShouldNotContain("backdoor", customMessage: "the critique is model-authored text — it must never reach the account");
    }

    /// <summary>A run abandoned after its push wrote no result at all: under a configured review there is no approval, so its pushed branch is withheld — and named, as one the review did not approve.</summary>
    [Fact]
    public void A_unit_with_no_result_under_a_configured_review_is_named_as_not_approved()
    {
        var runId = Guid.NewGuid();
        var task = JsonSerializer.Serialize(new AgentTask { Goal = "do the work", Harness = "codex-cli", OutputReviewMode = ReviewMode.Gate }, AgentJson.Options);

        RunIntegrationContributions.Withheld(Repo,
            new[] { Manifest(runId, Repo, PublishState.Pushed, branch: "codespace/agent/a") },
            new[] { new RunAgentWork(runId, "agent", "map#0", At(1), ResultJson: null, task) })
            .ShouldHaveSingleItem().ShouldBe(new WithheldContribution("agent#map#0", "output review did not approve it"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_unit_its_output_review_approved_or_never_configured_is_never_reported_withheld(bool reviewed)
    {
        var runId = Guid.NewGuid();
        var produced = new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", ChangedFiles = new[] { "src/Auth.cs" }, ProducedBranch = "codespace/agent/a" };
        var result = reviewed ? produced with { OutputReview = OutputReviewState.Approved } : produced;

        RunIntegrationContributions.Withheld(Repo,
            new[] { Manifest(runId, Repo, PublishState.Pushed, branch: "codespace/agent/a", acceptance: PublishAcceptanceState.Passed) },
            new[] { Work(runId, "agent", "map#0", minute: 1, JsonSerializer.Serialize(result, AgentJson.Options)) })
            .ShouldBeEmpty("a unit that reaches the candidate is not withheld");
    }

    /// <summary>Two verdicts can withhold one unit; the acceptance grade is the one named — it is the older, harder fact, and the account says one reason per unit.</summary>
    [Fact]
    public void When_the_acceptance_grade_and_the_output_review_both_rejected_a_unit_the_acceptance_grade_is_named()
    {
        var runId = Guid.NewGuid();
        var flagged = new AgentRunResult { Status = AgentRunStatus.NeedsReview, ExitReason = "output-flagged", ReviewFeedback = "hard-coded admin backdoor", OutputReview = OutputReviewState.Flagged, ProducedBranch = "codespace/agent/a" };

        RunIntegrationContributions.Withheld(Repo,
            new[] { Manifest(runId, Repo, PublishState.Pushed, branch: "codespace/agent/a", acceptance: PublishAcceptanceState.Failed) },
            new[] { Work(runId, "agent", "map#0", minute: 1, JsonSerializer.Serialize(flagged, AgentJson.Options)) })
            .ShouldHaveSingleItem().Reason.ShouldBe("acceptance Failed");
    }

    [Theory]
    [InlineData(PublishAcceptanceState.Passed)]
    [InlineData(PublishAcceptanceState.NotApplicable)]
    public void A_unit_the_head_kept_is_never_reported_withheld(PublishAcceptanceState state)
    {
        var runId = Guid.NewGuid();

        RunIntegrationContributions.Withheld(Repo,
            new[] { Manifest(runId, Repo, PublishState.Pushed, branch: "codespace/agent/a", acceptance: state) },
            new[] { Work(runId, "agent", "map#0", minute: 1) })
            .ShouldBeEmpty("a unit that reaches the candidate is not withheld — naming it would contradict the integration outcome");
    }

    /// <summary>The two views partition the produced units: every unit is either a contribution or withheld, never both and never neither — which is the property that makes "what landed" plus "what was withheld" a complete account.</summary>
    [Fact]
    public void What_landed_and_what_was_withheld_partition_the_produced_units()
    {
        var flunked = Guid.NewGuid();
        var passed = Guid.NewGuid();
        var manifests = new[]
        {
            Manifest(flunked, Repo, PublishState.Pushed, branch: "codespace/agent/flunked", acceptance: PublishAcceptanceState.Failed),
            Manifest(passed, Repo, PublishState.Pushed, branch: "codespace/agent/passed", acceptance: PublishAcceptanceState.Passed),
        };
        var work = new[] { Work(flunked, "agent", "map#0", minute: 1), Work(passed, "agent", "map#1", minute: 2) };

        RunIntegrationContributions.Build(Repo, manifests, work).Select(c => c.Label).ShouldBe(new[] { "agent#map#1" });
        RunIntegrationContributions.Withheld(Repo, manifests, work).Select(w => w.Label).ShouldBe(new[] { "agent#map#0" });
    }

    /// <summary>A retry respawns a fresh agent run, so a unit whose first attempt flunked and whose second passed has BOTH rows in the ledger — and its work landed. Reporting the flunked attempt would tell the reduce that delivered work was not delivered.</summary>
    [Fact]
    public void A_unit_whose_retry_landed_is_not_reported_withheld_for_its_abandoned_attempt()
    {
        var abandoned = Guid.NewGuid();
        var respawned = Guid.NewGuid();
        var manifests = new[]
        {
            Manifest(abandoned, Repo, PublishState.Pushed, acceptance: PublishAcceptanceState.Failed),
            Manifest(respawned, Repo, PublishState.Pushed, branch: "codespace/agent/a2", acceptance: PublishAcceptanceState.Passed),
        };
        var work = new[] { Work(abandoned, "agent", "map#0", minute: 1), Work(respawned, "agent", "map#0", minute: 7) };

        RunIntegrationContributions.Build(Repo, manifests, work).ShouldHaveSingleItem();
        RunIntegrationContributions.Withheld(Repo, manifests, work).ShouldBeEmpty("the unit's work reached the candidate through its second attempt");
    }

    [Fact]
    public void Every_failed_attempt_of_a_withheld_unit_is_reported_once()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();

        var withheld = RunIntegrationContributions.Withheld(Repo,
            new[]
            {
                Manifest(first, Repo, PublishState.Pushed, acceptance: PublishAcceptanceState.Failed),
                Manifest(second, Repo, PublishState.Pushed, acceptance: PublishAcceptanceState.Failed),
                Manifest(third, Repo, PublishState.Pushed, acceptance: PublishAcceptanceState.Failed),
            },
            new[] { Work(first, "agent", "map#0", minute: 1), Work(second, "agent", "map#0", minute: 4), Work(third, "agent", "map#0", minute: 8) });

        withheld.ShouldHaveSingleItem(customMessage: "the retries of one unit are one unit — a row per attempt would name it three times");
    }

    /// <summary>
    /// The ORDER is the contract: reduce to the latest attempt first, then apply the verdict. A retry respawns a fresh
    /// agent run, so a unit whose first attempt failed AFTER its acceptance passed (an executor fault past the grade and
    /// the push — the row names the pushed branch, the result is a bare Failed) and whose node was retried into a second
    /// attempt that flunked its acceptance has both rows in the ledger. Gating first let the superseded attempt's bytes
    /// integrate — the abandoned attempt on the candidate, the unit's own latest verdict ignored. Now the latest attempt
    /// decides: nothing integrates, and the unit is withheld at ITS verdict.
    /// </summary>
    [Fact]
    public void A_unit_whose_latest_attempt_flunked_contributes_nothing_although_an_earlier_attempt_passed()
    {
        var faulted = Guid.NewGuid();
        var retried = Guid.NewGuid();
        var executorFault = new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = AgentRunExecutor.GenericExecutorExitReason, Error = "the transcript attach faulted" };
        var flunked = new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = AgentAcceptanceContract.FailClosedExitReason, AcceptancePassed = false, Patch = "second-attempt-bytes" };
        var manifests = new[]
        {
            Manifest(faulted, Repo, PublishState.Pushed, branch: "codespace/agent/a1", acceptance: PublishAcceptanceState.Passed),
            Manifest(retried, Repo, PublishState.PatchOnly, acceptance: PublishAcceptanceState.Failed),
        };
        var work = new[]
        {
            Work(faulted, "agent", "map#0", minute: 1, JsonSerializer.Serialize(executorFault, AgentJson.Options), AgentRunStatus.Failed),
            Work(retried, "agent", "map#0", minute: 7, JsonSerializer.Serialize(flunked, AgentJson.Options), AgentRunStatus.Failed),
        };

        RunIntegrationContributions.Build(Repo, manifests, work).ShouldBeEmpty("the superseded attempt's branch never integrates, and the latest attempt was rejected");
        RunIntegrationContributions.Withheld(Repo, manifests, work).ShouldHaveSingleItem().ShouldBe(new WithheldContribution("agent#map#0", "acceptance Failed"));
        RunIntegrationContributions.LandedFailedSubtasks(Repo, manifests, work).ShouldBeEmpty("a unit that did not land is not a failure that landed — whatever its earlier attempt's row says");
    }

    // ─── Landed although the subtask's own run did not succeed ───────────────────────
    //
    // A manifest row is written whatever the owning attempt's status, and neither an ungraded unit's acceptance nor (with
    // no output review configured) a review holds it back — so a subtask that died after editing files integrates Clean
    // with its diff on the candidate. `LandedFailedSubtasks` names it, so the account of the candidate does not read as a
    // whole deliverable. How the run ended is its agent-run ROW's status, the column the map's own {"error": ...} entry
    // is read from: the result can be absent altogether (a worker the reconciler abandoned after its push).

    [Theory]
    [InlineData(AgentRunStatus.Failed)]
    [InlineData(AgentRunStatus.TimedOut)]
    [InlineData(AgentRunStatus.Cancelled)]
    [InlineData(AgentRunStatus.NeedsReview)]
    public void A_contribution_whose_own_run_did_not_succeed_is_reported_as_an_unverified_partial(AgentRunStatus status)
    {
        var runId = Guid.NewGuid();

        RunIntegrationContributions.LandedFailedSubtasks(Repo,
            new[] { Manifest(runId, Repo, PublishState.PatchOnly) },
            new[] { Work(runId, "agent", "map#1", minute: 1, ResultWith(status), status) })
            .ShouldBe(new[] { new LandedFailedSubtask("agent#map#1", AcceptancePassed: false) });
    }

    [Fact]
    public void A_contribution_whose_own_run_succeeded_is_never_reported()
    {
        var runId = Guid.NewGuid();

        RunIntegrationContributions.LandedFailedSubtasks(Repo,
            new[] { Manifest(runId, Repo, PublishState.Pushed, branch: "codespace/agent/a") },
            new[] { Work(runId, "agent", "map#0", minute: 1, ResultWith(AgentRunStatus.Succeeded), AgentRunStatus.Succeeded) })
            .ShouldBeEmpty();
    }

    /// <summary>
    /// The reconciler abandons a worker that died after the executor wrote its manifest row: the run row is marked Failed
    /// and NO result is written (the abandon's terminal write keeps the column null). The map reports that subtask as an
    /// error entry — from the row's status — so the account must flag it, and a result-keyed reading would call it whole.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{not-json")]
    public void A_run_abandoned_after_its_push_is_reported_although_it_left_no_readable_result(string? resultJson)
    {
        var runId = Guid.NewGuid();

        RunIntegrationContributions.LandedFailedSubtasks(Repo,
            new[] { Manifest(runId, Repo, PublishState.Pushed, branch: "codespace/agent/a") },
            new[] { Work(runId, "agent", "map#0", minute: 1, resultJson, AgentRunStatus.Failed) })
            .ShouldBe(new[] { new LandedFailedSubtask("agent#map#0", AcceptancePassed: false) });
    }

    /// <summary>The row's status is the verdict on the run: a run whose row says Succeeded is whole whatever its result bytes read, and a row that carries no status claims nothing.</summary>
    [Theory]
    [InlineData(AgentRunStatus.Succeeded, null)]
    [InlineData(AgentRunStatus.Succeeded, "{not-json")]
    [InlineData(null, null)]
    [InlineData(null, "{not-json")]
    public void A_row_that_is_not_marked_failed_claims_nothing_about_the_run(AgentRunStatus? status, string? resultJson)
    {
        var runId = Guid.NewGuid();

        RunIntegrationContributions.LandedFailedSubtasks(Repo,
            new[] { Manifest(runId, Repo, PublishState.Pushed, branch: "codespace/agent/a") },
            new[] { Work(runId, "agent", "map#0", minute: 1, resultJson, status) })
            .ShouldBeEmpty("an absent or unreadable result is not evidence that the run failed");
    }

    /// <summary>The executor grades acceptance only for a run that reported its own outcome, so a Passed row whose run still ended Failed — a fault after the grade, here the executor's own — was verified and failed AFTERWARDS: the diff on the branch is not an unchecked partial.</summary>
    [Fact]
    public void A_run_that_failed_after_its_acceptance_passed_is_reported_as_verified()
    {
        var runId = Guid.NewGuid();
        var fault = new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = AgentRunExecutor.GenericExecutorExitReason, Error = "the transcript attach faulted" };

        RunIntegrationContributions.LandedFailedSubtasks(Repo,
            new[] { Manifest(runId, Repo, PublishState.Pushed, branch: "codespace/agent/a", acceptance: PublishAcceptanceState.Passed) },
            new[] { Work(runId, "agent", "map#0", minute: 1, JsonSerializer.Serialize(fault, AgentJson.Options), AgentRunStatus.Failed) })
            .ShouldBe(new[] { new LandedFailedSubtask("agent#map#0", AcceptancePassed: true) });
    }

    /// <summary>A unit whose latest attempt was withheld did not land, so it is not a failure that landed: it is named once, by <c>Withheld</c>, and never by both views.</summary>
    [Fact]
    public void A_withheld_unit_is_never_reported_as_landed_because_it_did_not_land()
    {
        var runId = Guid.NewGuid();
        var manifests = new[] { Manifest(runId, Repo, PublishState.PatchOnly, acceptance: PublishAcceptanceState.Failed) };
        var work = new[] { Work(runId, "agent", "map#0", minute: 1, ResultWith(AgentRunStatus.Failed), AgentRunStatus.Failed) };

        RunIntegrationContributions.LandedFailedSubtasks(Repo, manifests, work).ShouldBeEmpty();
        RunIntegrationContributions.Withheld(Repo, manifests, work).ShouldHaveSingleItem();
    }

    /// <summary>Only the attempt that LANDED is asked: a first attempt that died and a retry that succeeded leave a unit whose work is whole.</summary>
    [Fact]
    public void Only_the_latest_attempts_run_is_asked_whether_it_succeeded()
    {
        var died = Guid.NewGuid();
        var retried = Guid.NewGuid();
        var manifests = new[] { Manifest(died, Repo, PublishState.PatchOnly), Manifest(retried, Repo, PublishState.Pushed, branch: "codespace/agent/a2") };
        var work = new[]
        {
            Work(died, "agent", "map#0", minute: 1, ResultWith(AgentRunStatus.Failed), AgentRunStatus.Failed),
            Work(retried, "agent", "map#0", minute: 7, ResultWith(AgentRunStatus.Succeeded), AgentRunStatus.Succeeded),
        };

        RunIntegrationContributions.LandedFailedSubtasks(Repo, manifests, work).ShouldBeEmpty("the unit's work is the retry's, and the retry succeeded");
    }

    [Fact]
    public void Landed_failures_list_in_agent_run_creation_order()
    {
        var late = Guid.NewGuid();
        var early = Guid.NewGuid();
        var manifests = new[] { Manifest(late, Repo, PublishState.PatchOnly), Manifest(early, Repo, PublishState.PatchOnly, acceptance: PublishAcceptanceState.Passed) };
        var work = new[]
        {
            Work(late, "agent", "map#2", minute: 9, ResultWith(AgentRunStatus.TimedOut), AgentRunStatus.TimedOut),
            Work(early, "agent", "map#0", minute: 3, ResultWith(AgentRunStatus.Failed), AgentRunStatus.Failed),
        };
        var expected = new[] { new LandedFailedSubtask("agent#map#0", AcceptancePassed: true), new LandedFailedSubtask("agent#map#2", AcceptancePassed: false) };

        RunIntegrationContributions.LandedFailedSubtasks(Repo, manifests, work).ShouldBe(expected);
        RunIntegrationContributions.LandedFailedSubtasks(Repo, manifests.Reverse().ToArray(), work.Reverse().ToArray()).ShouldBe(expected);
    }

    /// <summary>One attempt can leave a row per repository alias, all carrying the attempt's one acceptance verdict; they are one failed subtask, so one entry.</summary>
    [Fact]
    public void The_alias_rows_of_one_failed_attempt_are_one_entry()
    {
        var attempt = Guid.NewGuid();
        var manifests = new[]
        {
            Manifest(attempt, Repo, PublishState.PatchOnly, alias: "repo"),
            Manifest(attempt, Repo, PublishState.PatchOnly, alias: "repo-2"),
        };

        RunIntegrationContributions.LandedFailedSubtasks(Repo, manifests, new[] { Work(attempt, "agent", "map#0", minute: 1, ResultWith(AgentRunStatus.Failed), AgentRunStatus.Failed) })
            .ShouldHaveSingleItem().ShouldBe(new LandedFailedSubtask("agent#map#0", AcceptancePassed: false));
    }

    /// <summary>The supervisor lane's cell is a whole turn, so its K agents share one label while being K distinct deliverables: a peer that landed must not hide the one that was withheld, which is why "did the unit land" is keyed on the agent there and on the cell everywhere else.</summary>
    [Fact]
    public void A_supervisor_turns_withheld_agent_is_named_even_though_a_peer_sharing_its_cell_landed()
    {
        var alpha = Guid.NewGuid();
        var beta = Guid.NewGuid();
        var manifests = new[]
        {
            Manifest(alpha, Repo, PublishState.Pushed, branch: "codespace/agent/s1", acceptance: PublishAcceptanceState.Failed),
            Manifest(beta, Repo, PublishState.Pushed, branch: "codespace/agent/s2", acceptance: PublishAcceptanceState.Passed),
        };
        var work = new[] { PlannedSupervisorWork(alpha, "sup#turn1", "subtask-a", minute: 1), PlannedSupervisorWork(beta, "sup#turn1", "subtask-b", minute: 2) };

        RunIntegrationContributions.Build(Repo, manifests, work).ShouldHaveSingleItem().ProducedBranch.ShouldBe("codespace/agent/s2");
        RunIntegrationContributions.Withheld(Repo, manifests, work).ShouldHaveSingleItem().ShouldBe(new WithheldContribution("sup#sup#turn1", "acceptance Failed"));
    }

    /// <summary>A supervisor turn's K agents share ONE label but are K deliverables — so two withheld agents of one turn are two entries, each at its own verdict. (Keyed on the label they would be merged into one, and the second withheld deliverable would simply vanish from the account.)</summary>
    [Fact]
    public void Two_withheld_agents_of_one_supervisor_turn_are_two_entries_not_one()
    {
        var alpha = Guid.NewGuid();
        var beta = Guid.NewGuid();
        var manifests = new[]
        {
            Manifest(alpha, Repo, PublishState.Pushed, branch: "codespace/agent/s1", acceptance: PublishAcceptanceState.Failed),
            Manifest(beta, Repo, PublishState.Pushed, branch: "codespace/agent/s2", acceptance: PublishAcceptanceState.Waived),
        };
        var work = new[] { PlannedSupervisorWork(alpha, "sup#turn1", "subtask-a", minute: 1), PlannedSupervisorWork(beta, "sup#turn1", "subtask-b", minute: 2) };

        RunIntegrationContributions.Withheld(Repo, manifests, work).ShouldBe(new[]
        {
            new WithheldContribution("sup#sup#turn1", "acceptance Failed"),
            new WithheldContribution("sup#sup#turn1", "acceptance waived by the supervisor (not delivered)"),
        });
    }

    /// <summary>One attempt can leave a row per repository alias; they are one unit's one rejected attempt, so one entry.</summary>
    [Fact]
    public void The_alias_rows_of_one_withheld_attempt_are_one_entry()
    {
        var attempt = Guid.NewGuid();
        var manifests = new[]
        {
            Manifest(attempt, Repo, PublishState.Pushed, alias: "repo", acceptance: PublishAcceptanceState.Failed),
            Manifest(attempt, Repo, PublishState.Pushed, alias: "repo-2", acceptance: PublishAcceptanceState.Failed),
        };

        RunIntegrationContributions.Withheld(Repo, manifests, new[] { Work(attempt, "agent", "map#0", minute: 1) })
            .ShouldHaveSingleItem().ShouldBe(new WithheldContribution("agent#map#0", "acceptance Failed"));
    }

    [Fact]
    public void Only_work_this_repository_actually_produced_can_be_withheld_from_it()
    {
        var foreignRepo = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var nothingProduced = Guid.NewGuid();
        var mine = Guid.NewGuid();

        var withheld = RunIntegrationContributions.Withheld(Repo,
            new[]
            {
                Manifest(foreign, foreignRepo, PublishState.Pushed, acceptance: PublishAcceptanceState.Failed),
                Manifest(nothingProduced, Repo, PublishState.None, acceptance: PublishAcceptanceState.Failed),
                Manifest(mine, Repo, PublishState.Pushed, acceptance: PublishAcceptanceState.Failed),
            },
            new[] { Work(foreign, "agent", "map#0", minute: 1), Work(nothingProduced, "agent", "map#1", minute: 2), Work(mine, "agent", "map#2", minute: 3) });

        withheld.Select(w => w.Label).ShouldBe(new[] { "agent#map#2" }, "another repository's verdict, and a row that produced nothing, are not this repository's withheld work");
    }

    [Fact]
    public void Withheld_units_list_in_agent_run_creation_order_whatever_order_the_rows_arrive_in()
    {
        var late = Guid.NewGuid();
        var early = Guid.NewGuid();
        var middle = Guid.NewGuid();
        var manifests = new[] { late, early, middle }.Select(id => Manifest(id, Repo, PublishState.Pushed, acceptance: PublishAcceptanceState.Failed)).ToArray();
        var work = new[] { Work(late, "agent", "map#2", minute: 9), Work(early, "agent", "map#0", minute: 3), Work(middle, "agent", "map#1", minute: 6) };

        RunIntegrationContributions.Withheld(Repo, manifests, work).Select(w => w.Label).ShouldBe(new[] { "agent#map#0", "agent#map#1", "agent#map#2" });
        RunIntegrationContributions.Withheld(Repo, manifests.Reverse().ToArray(), work.Reverse().ToArray()).Select(w => w.Label).ShouldBe(new[] { "agent#map#0", "agent#map#1", "agent#map#2" });
    }

    [Fact]
    public void A_surviving_attempts_sibling_alias_rows_all_stay()
    {
        var attempt1 = Guid.NewGuid();
        var attempt2 = Guid.NewGuid();

        var contributions = RunIntegrationContributions.Build(Repo,
            new[]
            {
                Manifest(attempt1, Repo, PublishState.Pushed, alias: "repo"),
                Manifest(attempt2, Repo, PublishState.Pushed, patchArtifactId: Guid.NewGuid(), alias: "repo"),
                Manifest(attempt2, Repo, PublishState.Pushed, patchArtifactId: Guid.NewGuid(), alias: "repo-2"),
            },
            new[] { Work(attempt1, "agent", "map#0", minute: 1), Work(attempt2, "agent", "map#0", minute: 7) });

        contributions.Count.ShouldBe(2, "the reduction drops superseded ATTEMPTS, never the surviving attempt's own per-alias rows");
    }

    [Fact]
    public void The_order_and_the_surviving_attempt_repeat_across_builds()
    {
        var early = Guid.NewGuid();
        var retriedFirst = Guid.NewGuid();
        var retriedLast = Guid.NewGuid();

        // The two map#1 attempts share a LABEL, so a label-only assertion cannot see which one survived a reversal —
        // they carry distinct branches and bytes precisely so an order-dependent pick would fail this test.
        var manifests = new[]
        {
            Manifest(retriedLast, Repo, PublishState.Pushed, branch: "codespace/agent/a2"),
            Manifest(early, Repo, PublishState.Pushed, branch: "codespace/agent/e"),
            Manifest(retriedFirst, Repo, PublishState.Pushed, branch: "codespace/agent/a1"),
        };
        var work = new[] { Work(retriedLast, "agent", "map#1", minute: 8, Patch("finished")), Work(early, "agent", "map#0", minute: 2), Work(retriedFirst, "agent", "map#1", minute: 4, Patch("half-done")) };

        var first = RunIntegrationContributions.Build(Repo, manifests, work);
        var second = RunIntegrationContributions.Build(Repo, manifests.Reverse().ToArray(), work.Reverse().ToArray());

        first.Select(c => c.Label).ShouldBe(new[] { "agent#map#0", "agent#map#1" }, customMessage: "the surviving attempts still apply in agent-run creation order");
        second.Select(c => c.Label).ShouldBe(first.Select(c => c.Label), customMessage: "the apply order — and so the integration outcome — is a function of the rows, never of the query's row order");
        second.Select(c => c.ProducedBranch).ShouldBe(first.Select(c => c.ProducedBranch), customMessage: "WHICH attempt survives is a function of the rows too — reversing the query's row order must not swap the retried unit's branch");
        second.Select(c => c.Patch).ShouldBe(first.Select(c => c.Patch), customMessage: "and the bytes that reach the integrator with it");
        second.Last().ProducedBranch.ShouldBe("codespace/agent/a2", customMessage: "the newest attempt by agent-run creation is the one that survives, whichever order the rows arrived in");
    }

    // ─── The lane fence: a supervisor turn cell is not a unit ────────────────────────

    [Fact]
    public void A_supervisor_turns_parallel_agents_all_contribute()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        // RealSupervisorActionExecutor stamps ONE turn cell (<nodeId>#turn{N}) on every agent of a turn, so these two
        // share a (node, iteration) cell while being concurrent deliverables — reducing them drops real work.
        var contributions = RunIntegrationContributions.Build(Repo,
            new[] { Manifest(first, Repo, PublishState.Pushed, branch: "codespace/agent/s1"), Manifest(second, Repo, PublishState.Pushed, branch: "codespace/agent/s2") },
            new[] { PlannedSupervisorWork(first, "sup#turn1", "subtask-a", minute: 1), PlannedSupervisorWork(second, "sup#turn1", "subtask-b", minute: 2) });

        contributions.Select(c => c.ProducedBranch).ShouldBe(new[] { "codespace/agent/s1", "codespace/agent/s2" },
            customMessage: "the supervisor's K parallel agents share a TURN cell, not a unit — collapsing them to one silently loses K-1 unsuperseded contributions");
        contributions.Select(c => c.Label).ShouldBe(new[] { "sup#sup#turn1", "sup#sup#turn1" },
            customMessage: "and they still SHARE the turn-cell label downstream — the fence changes which rows survive, never what the run calls them");
    }

    [Fact]
    public void A_plan_less_supervisor_spawn_is_fenced_off_too()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        // The plan-lineage WorkUnit stamp is only written when a plan decision exists (Spawn.cs), so a plan-less
        // spawn's rows carry SubtaskId ALONE — a fence reading only WorkUnit would collapse this pair.
        var contributions = RunIntegrationContributions.Build(Repo,
            new[] { Manifest(first, Repo, PublishState.Pushed), Manifest(second, Repo, PublishState.Pushed) },
            new[] { SupervisorWork(first, "sup#turn1", "subtask-a", minute: 1), SupervisorWork(second, "sup#turn1", "subtask-b", minute: 2) });

        contributions.Count.ShouldBe(2, "SubtaskId alone marks the supervisor lane when no plan decision stamped a WorkUnit — the same K-1 loss otherwise returns through the plan-less spawn");
    }

    [Fact]
    public void An_unreadable_task_envelope_is_never_reduced()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var contributions = RunIntegrationContributions.Build(Repo,
            new[] { Manifest(first, Repo, PublishState.Pushed), Manifest(second, Repo, PublishState.Pushed) },
            new[] { new RunAgentWork(first, "agent", "map#0", At(1), null, "{not-json"), new RunAgentWork(second, "agent", "map#0", At(2), null, null) });

        contributions.Count.ShouldBe(2, "an unknown lane must not be reduced — an extra integrator conflict is routable, losing produced work is not");
    }

    // ─── Scope: the invariant is one attempt per (unit, REPOSITORY) ─────────────────

    [Fact]
    public void A_repository_the_surviving_attempt_never_touched_keeps_the_abandoned_attempts_work()
    {
        var abandoned = Guid.NewGuid();
        var respawned = Guid.NewGuid();
        var otherRepo = Guid.NewGuid();

        var manifests = new[]
        {
            Manifest(abandoned, Repo, PublishState.Pushed, branch: "codespace/agent/a1"),
            Manifest(abandoned, otherRepo, PublishState.Pushed, branch: "codespace/agent/a1-other"),
            Manifest(respawned, Repo, PublishState.Pushed, branch: "codespace/agent/a2"),
        };
        var work = new[] { Work(abandoned, "agent", "map#0", minute: 1), Work(respawned, "agent", "map#0", minute: 7) };

        RunIntegrationContributions.Build(Repo, manifests, work).ShouldHaveSingleItem().ProducedBranch.ShouldBe("codespace/agent/a2");

        RunIntegrationContributions.Build(otherRepo, manifests, work).ShouldHaveSingleItem(customMessage: "the reduction runs AFTER the repository filter — deliberately, since the retry produced nothing here to supersede this with")
            .ProducedBranch.ShouldBe("codespace/agent/a1-other");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static string Patch(string patch) =>
        JsonSerializer.Serialize(new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Patch = patch }, AgentJson.Options);

    /// <summary>A terminal result the executor could have recorded for a run that ended in <paramref name="status"/> having captured a diff.</summary>
    private static string ResultWith(AgentRunStatus status) =>
        JsonSerializer.Serialize(new AgentRunResult { Status = status, ExitReason = status == AgentRunStatus.Succeeded ? "completed" : "process-exited", Patch = "diff --git a/x b/x" }, AgentJson.Options);

    private static PublishManifest Manifest(Guid agentRunId, Guid repositoryId, PublishState state, string? branch = null, Guid? patchArtifactId = null, string alias = "primary", PublishAcceptanceState acceptance = PublishAcceptanceState.NotApplicable) => new()
    {
        Id = Guid.NewGuid(), TeamId = Guid.NewGuid(), Kind = PublishManifestKind.Agent, AgentRunId = agentRunId,
        RepositoryId = repositoryId, RepositoryAlias = alias, BaseSha = "base1", Branch = branch,
        PatchArtifactId = patchArtifactId, PublishStateValue = state, AcceptanceState = acceptance,
    };

    /// <summary>A row from a lane whose (node, iteration) cell IS the unit — a map branch / loop iteration / top-level node, whose task envelope carries no supervisor stamp.</summary>
    private static RunAgentWork Work(Guid agentRunId, string nodeId, string iterationKey, int minute, string? resultJson = null, AgentRunStatus? status = null) =>
        new(agentRunId, nodeId, iterationKey, At(minute), resultJson, TaskJson(subtaskId: null, workUnit: null), status);

    /// <summary>A row the SUPERVISOR staked on a plan-less spawn: its cell is the whole turn, and only <c>SubtaskId</c> names the agent's own unit.</summary>
    private static RunAgentWork SupervisorWork(Guid agentRunId, string turnCell, string subtaskId, int minute) =>
        new(agentRunId, "sup", turnCell, At(minute), null, TaskJson(subtaskId, workUnit: null));

    /// <summary>The plan-lineage <c>WorkUnit</c> stamp — the coordinate the completion composer fences on — carried ALONE. A real planned spawn writes it beside <c>SubtaskId</c>; isolating it here keeps that half of the fence independently falsifiable.</summary>
    private static RunAgentWork PlannedSupervisorWork(Guid agentRunId, string turnCell, string unitId, int minute) =>
        new(agentRunId, "sup", turnCell, At(minute), null, TaskJson(subtaskId: null, new WorkUnitRef { WorkPlanId = Guid.NewGuid(), PlanVersion = 1, UnitId = unitId }));

    private static string TaskJson(string? subtaskId, WorkUnitRef? workUnit) =>
        JsonSerializer.Serialize(new AgentTask { Goal = "do the work", Harness = "codex-cli", SubtaskId = subtaskId, WorkUnit = workUnit }, AgentJson.Options);

    private static DateTimeOffset At(int minute) => new(2026, 1, 1, 0, minute, 0, TimeSpan.Zero);
}
