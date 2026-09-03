using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Dtos.Decisions;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Agents.Benchmark;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The S5 OBJECTIVE oracle gate (<see cref="AgentRunExecutor.GradeAcceptanceIfPresentAsync"/>) — the
/// single-agent twin of the supervisor's per-unit fold gate. Fail-closed on a failing check or an ungradable
/// contract; byte-identical when no contract / non-success; the captured work is always preserved (the STATUS
/// tells the truth, the branch and diff stay for diagnosis). A multi-repo result is graded PER REPO
/// (<see cref="AgentRunExecutor.GradeMultiRepoAcceptanceAsync"/>), mirroring the supervisor lane's
/// <c>GradeUnitAcceptanceMultiRepoAsync</c> — a contract binds the WHOLE change, not just the primary repo.
/// </summary>
public class AgentRunExecutorAcceptanceTests
{
    [Fact]
    public async Task No_contract_is_byte_identical_and_never_grades()
    {
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "unused" });

        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(acceptance: null), Succeeded(), workspace: null, CancellationToken.None);

        result.AcceptancePassed.ShouldBeNull();
        grader.Calls.ShouldBe(0, "no contract ⇒ the grader is never invoked");
    }

    [Fact]
    public async Task An_all_blank_command_reads_as_no_contract()
    {
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "ok" });

        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(Spec(" ", "")), Succeeded(), workspace: null, CancellationToken.None);

        result.AcceptancePassed.ShouldBeNull();
        grader.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_self_reported_failure_that_produced_nothing_skips_the_gate()
    {
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "ok" });

        var failed = new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit", Error = "I could not finish." };
        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(Spec("sh", "check.sh")), failed, workspace: null, CancellationToken.None);

        result.ShouldBe(failed, "a failure with NO work has nothing to grade — byte-identical, exactly as before D4b");
        grader.Calls.ShouldBe(0);
        result.Contradiction.ShouldBeNull();
    }

    [Theory]
    [InlineData(AgentRunStatus.Cancelled)]
    [InlineData(AgentRunStatus.TimedOut)]
    [InlineData(AgentRunStatus.NeedsReview)]
    public async Task A_status_that_is_not_a_genuine_self_report_skips_the_gate_even_with_work(AgentRunStatus status)
    {
        // The SAME exact-status matching the supervisor lane's per-unit fold applies
        // (SupervisorTurnService.Rehydrate.ClassifyUnitContradiction): only "Succeeded" and "Failed" are claims about
        // the work. A cancelled / watchdog-killed / human-owed run never reached a verdict of its own, so grading it
        // would mint an objective verdict for an attempt that never claimed to be finished.
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "ok" });

        var nonReport = Succeeded() with { Status = status };
        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(Spec("sh", "check.sh")), nonReport, workspace: null, CancellationToken.None);

        result.ShouldBe(nonReport);
        grader.Calls.ShouldBe(0);
    }

    // ── D4b: a self-reported FAILURE that produced work is graded, and an under-claim is named ───────────

    [Fact]
    public async Task A_self_reported_failure_whose_check_passes_folds_to_succeeded_and_names_the_under_claim()
    {
        // The defect this closes: an agent that did the work but said "I couldn't finish" terminalized Failure with
        // the work discarded. The objective verdict OUTRANKS the claim — the same rule the supervisor lane's per-unit
        // fold already applies (an under-claimed unit with a PASSED gate folds as finished).
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "exit 0" });

        var claimed = FailedWithWork();
        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(Spec("sh", "check.sh")), claimed, workspace: null, CancellationToken.None);

        grader.PatchCalls.ShouldBe(1, "the work is there — grade the recorded patch instead of taking the agent's word for the failure");
        grader.Calls.ShouldBe(0, "a Failed run was never pushed, so there is no branch to grade — the S2 patch lane is the production path here");
        grader.LastPatchBaseSha.ShouldBe("deadbeef", "the run's own recorded base anchors the oracle, exactly as on the Succeeded lane");
        result.Status.ShouldBe(AgentRunStatus.Succeeded, "the check passed — the run delivered, whatever the agent believed");
        result.AcceptancePassed.ShouldBe(true);
        result.AcceptanceDetail.ShouldBe("exit 0");
        result.Contradiction.ShouldBe(AgentContradiction.UnderClaim, "the agent gave up on work that was actually fine");
        result.Error.ShouldBe(claimed.Error, "the agent's own words survive on the durable result — the status is corrected, the account is not rewritten");
        result.ExitReason.ShouldBe(claimed.ExitReason);
        result.Patch.ShouldBe(claimed.Patch, "the captured work is untouched by the fold");
    }

    [Fact]
    public async Task A_folded_under_claim_never_buys_a_revise_round()
    {
        // The revise loop exists to fix an oracle FAILURE; an under-claim has no failure to fix.
        var (executor, _) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "exit 0" });
        var task = TaskWith(Spec("sh", "check.sh"));

        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), task, FailedWithWork(), workspace: null, CancellationToken.None);

        AgentRunExecutor.ReviseReasonFor(task, result).ShouldBeNull();
    }

    [Fact]
    public async Task A_self_reported_failure_whose_check_also_fails_stays_failed_with_no_contradiction()
    {
        var evidenceId = Guid.NewGuid();
        var (executor, _) = NewExecutor(new BenchmarkGrade { Passed = false, Detail = "tests-failed-exit-1", EvidenceArtifactId = evidenceId });

        var claimed = FailedWithWork();
        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(Spec("sh", "check.sh")), claimed, workspace: null, CancellationToken.None);

        result.Status.ShouldBe(AgentRunStatus.Failed);
        result.AcceptancePassed.ShouldBe(false, "the check ran and rejected the work — that verdict is recorded, not left null");
        result.AcceptanceDetail.ShouldBe("tests-failed-exit-1");
        result.AcceptanceEvidenceId.ShouldBe(evidenceId);
        result.Contradiction.ShouldBeNull("the claim and the verdict AGREE — an over-claim stamp here would be a lie");
        result.Error.ShouldBe(claimed.Error, "the agent's own failure text is not overwritten by the fail-closed sentence");
        result.ExitReason.ShouldBe(claimed.ExitReason, "the run failed on its own report, not on a fail-closed re-grade");
    }

    [Fact]
    public async Task A_multi_repo_failure_whose_work_is_only_in_a_secondary_repo_is_still_graded()
    {
        // A multi-repo run's TOP-LEVEL fields carry the primary repo only. Reading just those, a failure whose work
        // landed in a secondary repo looks work-less — and would be waved through ungraded, discarding exactly the
        // work this gate exists to save.
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "exit 0" });

        var secondaryOnly = new AgentRunResult
        {
            Status = AgentRunStatus.Failed,
            ExitReason = "non-zero-exit",
            Error = "I could not finish the task.",
            RepositoryResults = new[]
            {
                new RepositoryRunResult { RepositoryId = Guid.NewGuid(), Alias = "web" },
                new RepositoryRunResult { RepositoryId = Guid.NewGuid(), Alias = "api", ProducedBranch = "agent/api", ChangedFiles = new[] { "api/a.cs" } },
            },
        };

        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(Spec("sh", "check.sh")), secondaryOnly, workspace: null, CancellationToken.None);

        grader.Calls.ShouldBe(1, "the repo that produced a branch is graded — the top-level fields' silence is not the run's whole truth");
        result.Status.ShouldBe(AgentRunStatus.Succeeded);
        result.Contradiction.ShouldBe(AgentContradiction.UnderClaim);
    }

    [Fact]
    public async Task A_vacuous_pass_never_overturns_a_self_reported_failure()
    {
        // ExpectsChanges=false + nothing gradeable is a pass by CONSTRUCTION — no check ran. Treating it as a verdict
        // would fold a run to Succeeded with an under-claim on the strength of nothing having been verified at all.
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "unused" });

        // Work present (changed files) but nothing GRADEABLE: no branch, and no base to anchor a patch on.
        var claimed = FailedWithWork() with { BaseSha = null, Patch = null };
        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(Spec("sh", "check.sh"), expectsChanges: false), claimed, workspace: null, CancellationToken.None);

        grader.Calls.ShouldBe(0);
        grader.PatchCalls.ShouldBe(0, "there was nothing to grade — no check ran");
        result.ShouldBe(claimed, "a vacuous pass is not a verdict; the run keeps its own outcome, byte-identical");
        result.Contradiction.ShouldBeNull("nothing was checked, so nothing contradicted the claim");
    }

    [Fact]
    public async Task A_self_reported_failure_whose_grade_is_infra_mints_no_verdict_at_all()
    {
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "unused" });
        grader.Throw = new InvalidOperationException("clone exploded");

        var claimed = FailedWithWork();
        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(Spec("sh", "check.sh")), claimed, workspace: null, CancellationToken.None);

        result.Status.ShouldBe(AgentRunStatus.Failed);
        result.AcceptancePassed.ShouldBeNull("an infra fault means the CHECK never ran — never mint a verdict from it");
        result.AcceptanceDetail.ShouldStartWith("grade-error:");
        result.Contradiction.ShouldBeNull();
    }

    [Fact]
    public async Task A_branch_grade_hands_the_runs_own_base_to_the_grader_as_the_oracle_anchor()
    {
        // C3: the branch lane used to DISCARD the base it had recorded, so the grader had nothing to restore the
        // check script from and graded whatever bytes the agent left behind under that name. The patch lane always
        // passed its base; this is the branch lane's twin.
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "ok" });

        var produced = Succeeded() with { BaseSha = "base1234base1234" };
        await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(Spec("sh", "check.sh")), produced, workspace: null, CancellationToken.None);

        grader.OracleBaseShaByBranch["agent/s5-test"].ShouldBe("base1234base1234", "without the anchor the agent's own edit of check.sh IS the judge");
    }

    [Fact]
    public async Task A_run_with_no_recorded_base_still_grades_exactly_as_before()
    {
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "ok" });

        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(Spec("sh", "check.sh")), Succeeded(), workspace: null, CancellationToken.None);

        grader.OracleBaseShaByBranch["agent/s5-test"].ShouldBeNull("a re-attached run with no surviving clone records no base — it grades unprotected, never fails closed for it");
        result.AcceptancePassed.ShouldBe(true);
    }

    // ── Multi-repo: graded PER REPO, mirroring the supervisor lane's per-unit multi-repo fold ─────────────

    [Fact]
    public async Task Each_repo_of_a_multi_repo_grade_is_anchored_on_its_own_base()
    {
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "exit 0" });

        var multi = Succeeded() with
        {
            RepositoryResults = new[]
            {
                new RepositoryRunResult { RepositoryId = Guid.NewGuid(), Alias = "web", ProducedBranch = "agent/web", BaseSha = "webbase1" },
                new RepositoryRunResult { RepositoryId = Guid.NewGuid(), Alias = "api", ProducedBranch = "agent/api", BaseSha = "apibase1" },
            },
        };

        await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(Spec("sh", "check.sh")), multi, workspace: null, CancellationToken.None);

        grader.OracleBaseShaByBranch["agent/web"].ShouldBe("webbase1");
        grader.OracleBaseShaByBranch["agent/api"].ShouldBe("apibase1", "one repo's base can never anchor another's oracle");
    }


    [Fact]
    public async Task A_multi_repo_result_grades_every_repo_that_produced_a_branch()
    {
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "exit 0" });

        var multi = Succeeded() with
        {
            RepositoryResults = new[]
            {
                new RepositoryRunResult { RepositoryId = Guid.NewGuid(), Alias = "web", ProducedBranch = "agent/web" },
                new RepositoryRunResult { RepositoryId = Guid.NewGuid(), Alias = "api", ProducedBranch = "agent/api" },
            },
        };

        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(Spec("sh", "check.sh")), multi, workspace: null, CancellationToken.None);

        result.Status.ShouldBe(AgentRunStatus.Succeeded);
        result.AcceptancePassed.ShouldBe(true, "every repo's own check passed — the run's acceptance is no longer left null on a multi-repo result");
        grader.Calls.ShouldBe(2, "each repo with a produced branch is graded independently");
    }

    [Fact]
    public async Task A_multi_repo_result_fails_closed_when_any_one_repo_fails_its_check()
    {
        var evidenceId = Guid.NewGuid();
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "exit 0" });
        grader.GradeByBranch["agent/api"] = new BenchmarkGrade { Passed = false, Detail = "exit 1", EvidenceArtifactId = evidenceId };

        var multi = Succeeded() with
        {
            RepositoryResults = new[]
            {
                new RepositoryRunResult { RepositoryId = Guid.NewGuid(), Alias = "web", ProducedBranch = "agent/web" },
                new RepositoryRunResult { RepositoryId = Guid.NewGuid(), Alias = "api", ProducedBranch = "agent/api" },
            },
        };

        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(Spec("sh", "check.sh")), multi, workspace: null, CancellationToken.None);

        result.Status.ShouldBe(AgentRunStatus.Failed, "a contract binds the WHOLE change — one repo failing its check fails the run, exactly like a single-repo failure");
        result.ExitReason.ShouldBe("acceptance-failed");
        result.AcceptancePassed.ShouldBe(false);
        result.AcceptanceDetail.ShouldBe("repo 'api': exit 1", "the failing repo's alias is named so the failure is diagnosable");
        result.AcceptanceEvidenceId.ShouldBe(evidenceId, "P5-2: the failing repo's evidence binding survives the aggregate (mirroring the single-repo fail path and the supervisor twin)");
    }

    [Fact]
    public async Task A_multi_repo_result_fails_closed_when_the_grader_throws_on_the_second_repo_not_just_the_first()
    {
        // The catch-and-degrade path must be reachable at ANY loop position — a bug that only manifests after ≥1
        // successful iteration (state leaking across iterations, a wrong alias in the log/detail) would go
        // undetected if every test's throw only ever hit repo #1.
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "exit 0" });
        grader.ThrowOnBranch["agent/api"] = new InvalidOperationException("clone exploded");

        var multi = Succeeded() with
        {
            RepositoryResults = new[]
            {
                new RepositoryRunResult { RepositoryId = Guid.NewGuid(), Alias = "web", ProducedBranch = "agent/web" },
                new RepositoryRunResult { RepositoryId = Guid.NewGuid(), Alias = "api", ProducedBranch = "agent/api" },
            },
        };

        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(Spec("sh", "check.sh")), multi, workspace: null, CancellationToken.None);

        result.Status.ShouldBe(AgentRunStatus.Failed);
        result.AcceptancePassed.ShouldBe(false);
        result.AcceptanceDetail.ShouldBe("repo 'api': grade-error: clone exploded", "the throw is caught at whichever repo it occurs on, named correctly — not swallowed or misattributed to repo #1");
        grader.Calls.ShouldBe(2, "the throw happens on the SECOND grader call — repo #1 (web) is genuinely graded first, proving loop position doesn't matter");
    }

    [Fact]
    public async Task A_multi_repo_grade_verdict_is_persisted_onto_every_repos_publish_manifest_row()
    {
        // The grade GradeMultiRepoAcceptanceAsync computes is worthless to the north-star scorecard unless it
        // actually reaches PublishManifest.AcceptanceState — PersistPublishManifestAsync previously hardcoded
        // acceptancePassed: null for every multi-repo row, discarding it. This pins the wiring directly.
        var (executor, manifests) = NewExecutorWithManifests(new BenchmarkGrade { Passed = true, Detail = "exit 0" });
        manifests.Grader.GradeByBranch["agent/api"] = new BenchmarkGrade { Passed = false, Detail = "exit 1" };

        var run = Run();
        var task = TaskWith(Spec("sh", "check.sh"));
        var multi = Succeeded() with
        {
            RepositoryResults = new[]
            {
                new RepositoryRunResult { RepositoryId = Guid.NewGuid(), Alias = "web", ProducedBranch = "agent/web" },
                new RepositoryRunResult { RepositoryId = Guid.NewGuid(), Alias = "api", ProducedBranch = "agent/api" },
            },
        };

        var graded = await executor.GradeAcceptanceIfPresentAsync(run, task, multi, workspace: null, CancellationToken.None);
        await executor.PersistPublishManifestAsync(run.Id, run, task, graded, claimedEpoch: 7, CancellationToken.None);

        manifests.Upserts.Count.ShouldBe(2, "one upsert per repo");
        manifests.Upserts.ShouldAllBe(u => u.AcceptanceState == PublishAcceptanceState.Failed,
            "the aggregate verdict (one repo failed → the whole contract failed) must land on EVERY repo's row, never a hardcoded NotApplicable/null");
    }

    [Fact]
    public async Task A_multi_repo_result_with_no_produced_branch_anywhere_and_expects_changes_false_is_a_vacuous_pass()
    {
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "unused" });

        var multi = Succeeded() with
        {
            RepositoryResults = new[] { new RepositoryRunResult { RepositoryId = Guid.NewGuid(), Alias = "docs" } },
        };

        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(Spec("sh", "check.sh"), expectsChanges: false), multi, workspace: null, CancellationToken.None);

        result.Status.ShouldBe(AgentRunStatus.Succeeded);
        result.AcceptancePassed.ShouldBe(true, "the correctly-predicted no-diff outcome is a pass, never a failure — same rule as the single-repo path");
        result.AcceptanceDetail.ShouldStartWith("not-applicable");
        grader.Calls.ShouldBe(0, "there is nothing to clone in any repo");
    }

    [Fact]
    public async Task A_multi_repo_result_with_no_produced_branch_anywhere_fails_closed_by_default()
    {
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "unused" });

        var multi = Succeeded() with
        {
            RepositoryResults = new[] { new RepositoryRunResult { RepositoryId = Guid.NewGuid(), Alias = "docs" } },
        };

        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(Spec("sh", "check.sh")), multi, workspace: null, CancellationToken.None);

        result.Status.ShouldBe(AgentRunStatus.Failed);
        result.AcceptanceDetail.ShouldBe("no-branch-or-repo");
        grader.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_contract_with_no_branch_and_no_patch_fails_closed_without_grading()
    {
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "ok" });

        var noBranch = Succeeded() with { ProducedBranch = null };
        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(Spec("sh", "check.sh")), noBranch, workspace: null, CancellationToken.None);

        result.Status.ShouldBe(AgentRunStatus.Failed);
        result.ExitReason.ShouldBe("acceptance-failed");
        result.AcceptancePassed.ShouldBe(false);
        result.AcceptanceDetail.ShouldBe("no-branch-or-repo");
        grader.Calls.ShouldBe(0, "there is nothing to clone — fail closed, never a phantom pass");
        grader.PatchCalls.ShouldBe(0);
        result.Contradiction.ShouldBe(AgentContradiction.OverClaim, "the run self-reported Succeeded (the gate's own precondition) but the contract could not be verified — an over-claim");
    }

    // ── S2: no branch, but a recorded patch (a PatchOnly-mode producer, or a policy-blocked push) ─────

    [Fact]
    public async Task A_contract_with_no_branch_but_a_recorded_patch_grades_via_the_patch_not_fail_closed()
    {
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "exit 0" });

        var patchOnly = SucceededPatchOnly();
        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(Spec("sh", "check.sh")), patchOnly, workspace: null, CancellationToken.None);

        result.Status.ShouldBe(AgentRunStatus.Succeeded, "the patch was gradeable — no reason to fail closed");
        result.AcceptancePassed.ShouldBe(true);
        result.AcceptanceDetail.ShouldBe("exit 0");
        grader.Calls.ShouldBe(0, "no branch → the branch-based path is never invoked");
        grader.PatchCalls.ShouldBe(1);
        grader.LastPatchBaseSha.ShouldBe(patchOnly.BaseSha);
        result.Contradiction.ShouldBeNull("self-report Succeeded + grade passed agree — nothing to flag");
    }

    [Fact]
    public async Task A_patch_based_grade_that_fails_regrades_the_run_to_failed_exactly_like_a_branch_failure()
    {
        var (executor, _) = NewExecutor(new BenchmarkGrade { Passed = false, Detail = "exit 1" });

        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(Spec("sh", "check.sh")), SucceededPatchOnly(), workspace: null, CancellationToken.None);

        result.Status.ShouldBe(AgentRunStatus.Failed);
        result.ExitReason.ShouldBe("acceptance-failed");
        result.AcceptancePassed.ShouldBe(false);
        result.AcceptanceDetail.ShouldBe("exit 1");
        result.Contradiction.ShouldBe(AgentContradiction.OverClaim);
    }

    [Fact]
    public async Task An_offloaded_patch_artifact_is_preferred_when_present_alongside_an_inline_patch()
    {
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "ok" });
        var artifactId = Guid.NewGuid();
        const string compatibilityCopy = "diff --git a/x b/x";

        await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(Spec("sh", "check.sh")),
            SucceededPatchOnly() with { Patch = compatibilityCopy, PatchArtifactId = artifactId }, workspace: null, CancellationToken.None);

        grader.PatchCalls.ShouldBe(1, "the pre-completion two-carrier result must reach the patch grader exactly once");
        grader.LastInlinePatch.ShouldBe(compatibilityCopy, "the executor retains its bounded compatibility copy until AgentRunService persists the terminal result");
        grader.LastPatchArtifactId.ShouldBe(artifactId, "the authoritative full artifact must reach the grader beside that copy");
    }

    // ── S2: no branch, no patch, expectsChanges decides the outcome ─────────────────────

    [Fact]
    public async Task No_branch_no_patch_and_expects_changes_false_is_a_vacuous_pass_not_a_failure()
    {
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "unused" });

        var noWork = Succeeded() with { ProducedBranch = null };
        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(Spec("sh", "check.sh"), expectsChanges: false), noWork, workspace: null, CancellationToken.None);

        result.Status.ShouldBe(AgentRunStatus.Succeeded, "nothing about the run itself went wrong — the STATUS is untouched");
        result.AcceptancePassed.ShouldBe(true, "the correctly-predicted no-diff outcome is a PASS, never a failure");
        result.AcceptanceDetail.ShouldStartWith("not-applicable");
        grader.Calls.ShouldBe(0);
        grader.PatchCalls.ShouldBe(0);
        result.Contradiction.ShouldBeNull("a vacuous pass agrees with the self-report — no oracle actually ran to disagree");
    }

    [Fact]
    public async Task No_branch_no_patch_and_expects_changes_true_explicitly_fails_closed_exactly_like_the_default()
    {
        var (executor, _) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "unused" });

        var noWork = Succeeded() with { ProducedBranch = null };
        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(Spec("sh", "check.sh"), expectsChanges: true), noWork, workspace: null, CancellationToken.None);

        result.Status.ShouldBe(AgentRunStatus.Failed);
        result.AcceptanceDetail.ShouldBe("no-branch-or-repo");
    }

    [Fact]
    public async Task Expects_changes_false_is_ignored_when_a_branch_or_patch_actually_exists()
    {
        // false only excuses an ABSENCE — it never suppresses grading real, present work.
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "exit 0" });

        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(Spec("sh", "check.sh"), expectsChanges: false), Succeeded(), workspace: null, CancellationToken.None);

        result.AcceptancePassed.ShouldBe(true);
        result.AcceptanceDetail.ShouldBe("exit 0", "the branch was graded for real — not waved through as not-applicable");
        grader.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task A_passing_check_stamps_the_verdict_and_keeps_the_run_succeeded()
    {
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "exit 0" });

        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(Spec("sh", "check.sh")), Succeeded(), workspace: null, CancellationToken.None);

        result.Status.ShouldBe(AgentRunStatus.Succeeded);
        result.AcceptancePassed.ShouldBe(true);
        result.AcceptanceDetail.ShouldBe("exit 0");
        grader.Calls.ShouldBe(1);
        grader.LastCommand.ShouldBe(new[] { "sh", "check.sh" });
        result.Contradiction.ShouldBeNull("self-report Succeeded + grade passed agree");
    }

    [Fact]
    public async Task A_failing_check_regrades_the_run_to_failed_but_preserves_the_work()
    {
        var (executor, _) = NewExecutor(new BenchmarkGrade { Passed = false, Detail = "exit 1" });

        var succeeded = Succeeded();
        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(Spec("sh", "check.sh")), succeeded, workspace: null, CancellationToken.None);

        result.Status.ShouldBe(AgentRunStatus.Failed, "an objective oracle failing means the contract was NOT met — Failed is the truth");
        result.ExitReason.ShouldBe("acceptance-failed");
        result.AcceptancePassed.ShouldBe(false);
        result.AcceptanceDetail.ShouldBe("exit 1");
        result.ProducedBranch.ShouldBe(succeeded.ProducedBranch, "the captured work survives for diagnosis");
        result.Error.ShouldNotBeNull();
        result.Contradiction.ShouldBe(AgentContradiction.OverClaim, "the agent believed it was Succeeded; the objective check disagreed");
    }

    [Fact]
    public async Task A_grader_error_fails_closed_rather_than_crashing_the_completion()
    {
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "ok" });
        grader.Throw = new InvalidOperationException("clone exploded");

        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(Spec("sh", "check.sh")), Succeeded(), workspace: null, CancellationToken.None);

        result.Status.ShouldBe(AgentRunStatus.Failed);
        result.AcceptanceDetail.ShouldStartWith("grade-error:");
        result.Contradiction.ShouldBe(AgentContradiction.OverClaim, "a grader error still fails closed via FailClosed — the same over-claim correction, regardless of WHY the grade came back false");
    }

    [Fact]
    public async Task Blank_command_entries_are_dropped_before_grading()
    {
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "ok" });

        await executor.GradeAcceptanceIfPresentAsync(Run(), TaskWith(Spec("sh", " ", "check.sh", "")), Succeeded(), workspace: null, CancellationToken.None);

        grader.LastCommand.ShouldBe(new[] { "sh", "check.sh" });
    }

    // ─── C2: the repo-less lane, now that EVERY repo-less run has a scratch world ─────────────────────────

    /// <summary>
    /// A repo-less run's scratch world used to exist only when its contract declared deliverable paths, which meant
    /// a TestsPass contract could never reach the directory oracle. C2 gives every repo-less run a world (the
    /// undeclared walk needs one), so the kind rule has to be explicit — or a bare <c>exit 0</c> check would run in
    /// a directory of documents and pass VACUOUSLY, inventing a green verdict out of a category error.
    /// </summary>
    [Fact]
    public async Task A_repo_less_tests_pass_contract_stays_fail_closed_even_though_a_scratch_world_now_exists()
    {
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "must-not-be-consulted" });

        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), RepoLessTaskWith(Spec("sh", "check.sh")), SucceededRepoLess(), new FakeScratchWorkspace(), CancellationToken.None);

        result.AcceptancePassed.ShouldBe(false);
        result.AcceptanceDetail.ShouldBe("no-branch-or-repo", "the exact detail this lane has always failed closed on");
        grader.DirectoryCalls.ShouldBe(0, "an argv oracle must never be pointed at a directory of captured documents");
    }

    [Fact]
    public async Task A_repo_less_deliverable_contract_grades_against_the_scratch_world()
    {
        var (executor, grader) = NewExecutor(new BenchmarkGrade { Passed = true, Detail = "artifact-present" });

        var spec = new SupervisorAcceptanceSpec { Command = new[] { "report.md" }, Kind = BenchmarkGradingKind.ArtifactPresent };
        var result = await executor.GradeAcceptanceIfPresentAsync(Run(), RepoLessTaskWith(spec), SucceededRepoLess(), new FakeScratchWorkspace(), CancellationToken.None);

        result.AcceptancePassed.ShouldBe(true);
        result.AcceptanceDetail.ShouldBe("artifact-present");
        grader.DirectoryCalls.ShouldBe(1, "the still-alive scratch directory IS the world — the same ONE directory oracle the supervisor fold rebuilds one for");
    }

    // ─── fixtures ────────────────────────────────────────────────────────────────

    private static AgentRun Run() => new() { Id = Guid.NewGuid(), TeamId = Guid.NewGuid() };

    private static AgentTask RepoLessTaskWith(SupervisorAcceptanceSpec acceptance) =>
        new() { Goal = "write the findings report", Harness = "codex-cli", RepositoryId = null, Acceptance = acceptance };

    /// <summary>A repo-less producer: no repository, so no branch and no diff — the report it wrote lives in the scratch world alone.</summary>
    private static AgentRunResult SucceededRepoLess() => new() { Status = AgentRunStatus.Succeeded, ExitReason = "completed" };

    /// <summary>A scratch handle's shape as the grade gate reads it: a directory, and NO repositories (the discriminator every git-shaped step skips on).</summary>
    private sealed class FakeScratchWorkspace : Core.Services.Agents.Workspace.IWorkspaceHandle
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "cs-scratch-fake-" + Guid.NewGuid().ToString("N"));

        public IReadOnlyList<Core.Services.Agents.Workspace.WorkspaceRepositoryHandle> Repositories { get; } = Array.Empty<Core.Services.Agents.Workspace.WorkspaceRepositoryHandle>();

        public string PrimaryAlias => "";

        public Task<WorkspaceChanges> CaptureChangesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkspaceChanges> CaptureChangesAsync(string alias, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static AgentTask TaskWith(SupervisorAcceptanceSpec? acceptance, bool? expectsChanges = null) =>
        new() { Goal = "g", Harness = "codex-cli", RepositoryId = Guid.NewGuid(), Acceptance = acceptance, ExpectsChanges = expectsChanges };

    private static SupervisorAcceptanceSpec Spec(params string[] command) => new() { Command = command };

    private static AgentRunResult Succeeded() => new()
    {
        Status = AgentRunStatus.Succeeded,
        ExitReason = "completed",
        ProducedBranch = "agent/s5-test",
        ChangedFiles = new[] { "a.cs" },
    };

    /// <summary>
    /// D4b's PRODUCTION shape: the agent SAID it failed and left a recorded diff — and NO produced branch, because
    /// <c>PushProducedBranchIfEnabledAsync</c> skips a Failed run, so the work is only ever published AFTER the fold
    /// overturns the claim. A branch here would send every arm down the branch lane while production takes the
    /// PATCH lane (<c>GradePatchAsync</c>) — a fixture production cannot produce.
    /// </summary>
    private static AgentRunResult FailedWithWork() => new()
    {
        Status = AgentRunStatus.Failed,
        ExitReason = "non-zero-exit",
        Error = "I could not finish the task.",
        Summary = "Gave up before verifying.",
        ProducedBranch = null,
        ChangedFiles = new[] { "a.cs" },
        BaseSha = "deadbeef",
        Patch = "diff --git a/a.cs b/a.cs\n",
    };

    /// <summary>A patch-only producer (PR-2 policy, or a guard-blocked push): no pushed branch, but a real recorded diff a patch-based grade can act on.</summary>
    private static AgentRunResult SucceededPatchOnly() => new()
    {
        Status = AgentRunStatus.Succeeded,
        ExitReason = "completed",
        ProducedBranch = null,
        ChangedFiles = new[] { "a.cs" },
        BaseSha = "deadbeef",
        Patch = "diff --git a/a.cs b/a.cs\n",
    };

    private static (AgentRunExecutor Executor, FakeGrader Grader) NewExecutor(BenchmarkGrade grade)
    {
        var grader = new FakeGrader { Grade = grade };
        var executor = new AgentRunExecutor(null!, null!, null!, null!, null!, null!, null!, null!, new FakeScopeFactory(grader), null!, null!, null!, null!, null!, null!, new FakeCaptureIntentService(), null!, NullLogger<AgentRunExecutor>.Instance);
        return (executor, grader);
    }

    /// <summary>Like <see cref="NewExecutor"/> but also wires a real (fake) <see cref="IPublishManifestStore"/> — needed only by the manifest-persistence test, which is why every other test uses the simpler helper with <c>manifests: null!</c>.</summary>
    private static (AgentRunExecutor Executor, FakePublishManifestStore Manifests) NewExecutorWithManifests(BenchmarkGrade grade)
    {
        var grader = new FakeGrader { Grade = grade };
        var manifests = new FakePublishManifestStore(grader);
        var executor = new AgentRunExecutor(null!, null!, null!, null!, null!, null!, null!, null!, new FakeScopeFactory(grader), null!, null!, null!, null!, manifests, null!, new FakeCaptureIntentService(), null!, NullLogger<AgentRunExecutor>.Instance);
        return (executor, manifests);
    }

    private sealed class FakeGrader : ISupervisorAcceptanceGrader
    {
        public BenchmarkGrade Grade { get; set; } = new() { Passed = true, Detail = "ok" };

        /// <summary>Per-branch override, checked before the shared <see cref="Grade"/> — lets a multi-repo test make ONE repo's check fail while the others pass.</summary>
        public Dictionary<string, BenchmarkGrade> GradeByBranch { get; } = new();

        /// <summary>Per-branch throw, checked before <see cref="GradeByBranch"/> — lets a multi-repo test make the grader throw on a SPECIFIC repo (e.g. the second one), proving the catch-and-degrade path is reachable at any loop position, not just repo #1.</summary>
        public Dictionary<string, Exception> ThrowOnBranch { get; } = new();

        public Exception? Throw { get; set; }
        public int Calls { get; private set; }
        public IReadOnlyList<string>? LastCommand { get; private set; }

        public int PatchCalls { get; private set; }
        public string? LastPatchBaseSha { get; private set; }
        public string? LastInlinePatch { get; private set; }
        public Guid? LastPatchArtifactId { get; private set; }

        /// <summary>C3 — the oracle anchor each branch grade was handed, keyed by branch. Null (or an absent key) means that grade ran with no protection at all.</summary>
        public Dictionary<string, string?> OracleBaseShaByBranch { get; } = new();

        public Task<BenchmarkGrade> GradeAsync(Guid repositoryId, Guid teamId, string branch, SupervisorAcceptanceSpec spec, int timeoutSeconds, string? oracleBaseSha, CancellationToken cancellationToken)
        {
            OracleBaseShaByBranch[branch] = oracleBaseSha;
            return GradeAsync(repositoryId, teamId, branch, spec, timeoutSeconds, cancellationToken);
        }

        public Task<BenchmarkGrade> GradeAsync(Guid repositoryId, Guid teamId, string branch, SupervisorAcceptanceSpec spec, int timeoutSeconds, CancellationToken cancellationToken)
        {
            Calls++;
            LastCommand = spec.Command;

            if (Throw is { } ex) throw ex;
            if (ThrowOnBranch.TryGetValue(branch, out var branchEx)) throw branchEx;

            return System.Threading.Tasks.Task.FromResult(GradeByBranch.TryGetValue(branch, out var g) ? g : Grade);
        }

        public Task<BenchmarkGrade> GradePatchAsync(Guid repositoryId, Guid teamId, string baseSha, string inlinePatch, Guid? patchArtifactId, SupervisorAcceptanceSpec spec, int timeoutSeconds, CancellationToken cancellationToken)
        {
            PatchCalls++;
            LastPatchBaseSha = baseSha;
            LastInlinePatch = inlinePatch;
            LastPatchArtifactId = patchArtifactId;
            LastCommand = spec.Command;

            if (Throw is { } ex) throw ex;

            return System.Threading.Tasks.Task.FromResult(Grade);
        }

        public Task<BenchmarkGrade> GradeBaseAsync(Guid repositoryId, Guid teamId, string baseSha, SupervisorAcceptanceSpec spec, int timeoutSeconds, CancellationToken cancellationToken) =>
            Task.FromResult(new BenchmarkGrade { Passed = true, Detail = "baseline-tests-passed" });

        /// <summary>C2 — the repo-less directory lane. Counted separately from <see cref="Calls"/> so a test can assert an argv oracle was never pointed at a scratch world.</summary>
        public int DirectoryCalls { get; private set; }

        public Task<BenchmarkGrade> GradeDirectoryAsync(string directory, SupervisorAcceptanceSpec spec, Guid teamId, int timeoutSeconds, CancellationToken cancellationToken)
        {
            DirectoryCalls++;
            LastCommand = spec.Command;

            if (Throw is { } ex) throw ex;

            return Task.FromResult(Grade);
        }
    }

    /// <summary>Records every upsert (never persists — an in-memory list is enough to assert the AcceptanceState wiring). Shares the SAME <see cref="FakeGrader"/> the executor's DI scope resolves, so <see cref="NewExecutorWithManifests"/> can script per-branch grades exactly like <see cref="NewExecutor"/>'s callers do.</summary>
    private sealed class FakePublishManifestStore : IPublishManifestStore
    {
        public FakePublishManifestStore(FakeGrader grader) => Grader = grader;

        public FakeGrader Grader { get; }
        public List<PublishManifestUpsert> Upserts { get; } = new();

        public List<long> FencedEpochs { get; } = new();

        public Task UpsertForAgentRunAsync(Guid agentRunId, PublishManifestUpsert input, long expectedFenceEpoch, CancellationToken cancellationToken)
        {
            FencedEpochs.Add(expectedFenceEpoch);
            return UpsertForAgentRunAsync(agentRunId, input, cancellationToken);
        }

        public Task UpsertForAgentRunAsync(Guid agentRunId, PublishManifestUpsert input, CancellationToken cancellationToken)
        {
            Upserts.Add(input);
            return Task.CompletedTask;
        }

        public Task UpsertForIntegrationAsync(PublishManifestUpsert input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StampAcceptanceForAgentRunAsync(Guid agentRunId, PublishAcceptanceState state, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<PublishManifest>> ListForAgentRunAsync(Guid agentRunId, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<PublishManifest>>> ListForAgentRunsAsync(IReadOnlyCollection<Guid> agentRunIds, Guid teamId, int maxAgentRunIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<PublishManifest>> ListForWorkflowRunAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<PublishManifest>>> ListForWorkflowRunsAsync(IReadOnlyCollection<Guid> workflowRunIds, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeScopeFactory : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        private readonly ISupervisorAcceptanceGrader _grader;
        public FakeScopeFactory(ISupervisorAcceptanceGrader grader) { _grader = grader; }

        public IServiceScope CreateScope() => this;
        public IServiceProvider ServiceProvider => this;
        public object? GetService(Type serviceType) =>
            serviceType == typeof(ISupervisorAcceptanceGrader) ? _grader
            : serviceType == typeof(CodeSpace.Core.Services.Agents.Mcp.IToolCallLedgerService) ? new NoBlockingLedger()
            : null;
        public void Dispose() { }
    }

    /// <summary>No blocking decision — the gate's A1 defer falls through to the grade (mirrors the output-review test's ledger fake).</summary>
    private sealed class NoBlockingLedger : IToolCallLedgerService
    {
        public Task<Guid?> FindBlockingDecisionIdAsync(Guid agentRunId, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);

        public Task<ToolCallClaim> TryClaimAsync(Guid agentRunId, Guid teamId, string toolKind, string idempotencyKey, string inputHash, long fenceEpoch, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RecordTerminalAsync(Guid ledgerId, Guid teamId, ToolCallLedgerStatus status, string? resultJson, string? error, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryBeginApprovalAsync(Guid ledgerId, Guid teamId, string approvalToken, DateTimeOffset deadlineAt, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetApprovalMessageAsync(Guid ledgerId, Guid teamId, Guid messageId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryBeginExecutionAsync(Guid ledgerId, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ToolCallApprovalState?> ReadApprovalStateAsync(Guid ledgerId, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ToolCallTerminalReplayState?> ReadTerminalForReplayAsync(Guid ledgerId, Guid agentRunId, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryAnswerDecisionAsync(Guid ledgerId, Guid teamId, string answerJson, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetDecisionEnvelopeAsync(Guid ledgerId, Guid teamId, string envelopeJson, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ExpiredToolApproval>> ExpireStaleApprovalsAsync(DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<TimedOutDecision>> ExpireStaleDecisionsAsync(DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> ExpireStaleToolCallsAsync(DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> CountPendingDecisionsAsync(Guid agentRunId, Guid teamId, string excludeIdempotencyKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ToolCallLedger>> GetForRunAsync(Guid agentRunId, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
