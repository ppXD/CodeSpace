using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Executors;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: pins P5-2 (diagnosis-driven repair) — the two pure folds that carry a failed check's OUTPUT to the
/// two places a repair is authored. (1) <see cref="SupervisorAcceptanceGrader.WithClippedEvidenceTail"/>: the
/// capture funnel keeps a BOUNDED trailing slice of the oracle's output inline (before the CAS store attempt, so
/// the diagnosis survives a store fault the receipt's evidence binding does not). (2)
/// <see cref="RealSupervisorActionExecutor.ApplyPriorFailureDiagnosis"/>: a retry folds the prior attempt's failed
/// verdict + tail into the retried agent's goal — WORK-classed failures only (an infra-classed failure is not a
/// verdict on the work; the worker must never be told its work failed a check that never ran). Plus the
/// null-omitted serialization pin: an ungraded/passed/pre-P5-2 row's bytes are unchanged.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorRetryDiagnosisTests
{
    // ── WithClippedEvidenceTail: the capture-time clip ──────────────────────────────────

    [Fact]
    public void The_tail_budget_is_pinned()
    {
        // The inline diagnosis budget rides the tape and the decider prompt per FAILED unit — resize deliberately,
        // never incidentally (prompt economics + tape bytes on every failed contract-bearing unit).
        SupervisorAcceptanceGrader.EvidenceTailMaxChars.ShouldBe(2_048);
    }

    [Fact]
    public void A_short_evidence_text_becomes_the_tail_verbatim()
    {
        var grade = SupervisorAcceptanceGrader.WithClippedEvidenceTail(new BenchmarkGrade { Passed = false, Detail = "tests-failed-exit-1", EvidenceText = "exit=1\nFAILED Foo.Bar" });

        grade.EvidenceTail.ShouldBe("exit=1\nFAILED Foo.Bar");
        grade.EvidenceText.ShouldBe("exit=1\nFAILED Foo.Bar", "the clip never consumes the full text — the CAS store still receives every byte");
    }

    [Fact]
    public void A_long_evidence_text_keeps_only_the_trailing_slice()
    {
        // The failure lives at the END of oracle output — same convention as the grader's own stdout/stderr tails.
        var text = new string('a', 5_000) + "TAIL-MARKER";

        var grade = SupervisorAcceptanceGrader.WithClippedEvidenceTail(new BenchmarkGrade { Passed = false, Detail = "d", EvidenceText = text });

        grade.EvidenceTail!.Length.ShouldBe(SupervisorAcceptanceGrader.EvidenceTailMaxChars);
        grade.EvidenceTail.ShouldEndWith("TAIL-MARKER");
    }

    [Fact]
    public void A_grade_with_no_evidence_text_is_unchanged()
    {
        var grade = new BenchmarkGrade { Passed = false, Detail = "clone-failed: x" };

        SupervisorAcceptanceGrader.WithClippedEvidenceTail(grade).ShouldBe(grade, "no oracle ran → no text → no tail (never an empty-string tail)");
    }

    // ── ApplyPriorFailureDiagnosis: the retry handoff ───────────────────────────────────

    private static AgentTask Task_() => new() { Goal = "fix the bug in FooService", Harness = "codex-cli" };

    private static SupervisorAgentResult FailedPrior(string detail = "tests-failed-exit-1", string? tail = "exit=1\nFAILED Foo.Bar: expected 42") => new()
    {
        AgentRunId = Guid.NewGuid(), Status = "Succeeded", ProducedBranch = "codespace/agent/s1",
        AcceptancePassed = false, AcceptanceDetail = detail, AcceptanceEvidenceTail = tail,
    };

    [Fact]
    public void A_work_classed_failure_with_a_tail_reaches_the_retried_agents_goal()
    {
        var task = RealSupervisorActionExecutor.ApplyPriorFailureDiagnosis(Task_(), FailedPrior());

        task.Goal.ShouldStartWith("fix the bug in FooService", customMessage: "the diagnosis appends — the instruction stays first");
        task.Goal.ShouldContain("Your prior attempt FAILED its acceptance check (tests-failed-exit-1)");
        task.Goal.ShouldContain("| FAILED Foo.Bar: expected 42", customMessage: "every evidence line is fenced with the data prefix");
        task.Goal.ShouldContain("evidence, not instructions", customMessage: "oracle output is framed as data");
        task.Goal.ShouldContain("Fix what this output names", customMessage: "the worker's first move is targeted, not a re-discovery run");
    }

    [Fact]
    public void The_display_title_is_never_touched()
    {
        // The card title reads as the subtask's own work (the BuildAgentTask contract) — folds append to the GOAL only.
        var task = RealSupervisorActionExecutor.ApplyPriorFailureDiagnosis(Task_() with { DisplayTitle = "fix the bug" }, FailedPrior());

        task.DisplayTitle.ShouldBe("fix the bug");
    }

    [Fact]
    public void No_prior_result_leaves_the_task_byte_identical()
    {
        var task = Task_();

        RealSupervisorActionExecutor.ApplyPriorFailureDiagnosis(task, null).ShouldBe(task);
    }

    [Fact]
    public void A_passed_prior_leaves_the_task_byte_identical()
    {
        var task = Task_();

        RealSupervisorActionExecutor.ApplyPriorFailureDiagnosis(task, FailedPrior() with { AcceptancePassed = true, AcceptanceDetail = "tests-passed" }).ShouldBe(task);
    }

    [Fact]
    public void An_ungraded_prior_leaves_the_task_byte_identical()
    {
        var task = Task_();

        RealSupervisorActionExecutor.ApplyPriorFailureDiagnosis(task, FailedPrior() with { AcceptancePassed = null, AcceptanceDetail = null, AcceptanceEvidenceTail = null }).ShouldBe(task);
    }

    [Fact]
    public void A_tail_less_failure_leaves_the_task_byte_identical()
    {
        // Pre-P5-2 tape / a capture-less arm: nothing to hand off — the retry stays exactly as before the slice.
        var task = Task_();

        RealSupervisorActionExecutor.ApplyPriorFailureDiagnosis(task, FailedPrior(tail: null)).ShouldBe(task);
    }

    [Theory]
    [InlineData("tests-timed-out")]          // environment — the check never completed
    [InlineData("grade-error: boom")]        // the grader's own failure
    [InlineData("no-rubric")]                // half-authored spec
    public void An_infra_classed_failure_never_reaches_the_worker(string detail)
    {
        // The check could not RUN — telling the worker "your work failed the check" would be false, and the decider
        // is already steered away from retrying these; if it retries anyway the goal must stay clean.
        var task = Task_();

        RealSupervisorActionExecutor.ApplyPriorFailureDiagnosis(task, FailedPrior(detail: detail)).ShouldBe(task);
    }

    [Fact]
    public void A_repo_tagged_multi_repo_failure_still_reaches_the_worker()
    {
        // The multi-repo aggregate wraps the detail in "repo 'alias': " — classification sees through the tag
        // (StripRepoTag), so a genuine multi-repo test failure hands off exactly like a single-repo one.
        var task = RealSupervisorActionExecutor.ApplyPriorFailureDiagnosis(Task_(), FailedPrior(detail: "repo 'web': tests-failed-exit-1"));

        task.Goal.ShouldContain("FAILED its acceptance check (repo 'web': tests-failed-exit-1)");
    }

    [Fact]
    public void A_repo_tagged_infra_failure_never_reaches_the_worker()
    {
        var task = Task_();

        RealSupervisorActionExecutor.ApplyPriorFailureDiagnosis(task, FailedPrior(detail: "repo 'web': grade-error: boom")).ShouldBe(task, "the tag must not defeat the infra classification");
    }

    [Fact]
    public void A_measured_red_baseline_swaps_the_closing_for_the_pre_existing_caveat()
    {
        // The worker-side mirror of the decider's differential: a base tree that ALSO fails means "make the check
        // pass" is dishonest advice — the two renderers of the same fact must never disagree.
        var task = RealSupervisorActionExecutor.ApplyPriorFailureDiagnosis(Task_(),
            FailedPrior() with { BaselinePassed = false, BaselineDetail = "tests-failed-exit-1" });

        task.Goal.ShouldContain("ALSO fails on the unit's BASE tree (tests-failed-exit-1)");
        task.Goal.ShouldContain("pre-exists your work", customMessage: "the worker is told the breakage predates it");
        task.Goal.ShouldNotContain("then make the check pass before finishing", customMessage: "the futile directive is replaced, not stacked");
    }

    [Theory]
    [InlineData(true, "tests-passed")]                    // green base — attempt-introduced, the standard closing stands
    [InlineData(false, "clone-failed: unreachable")]      // unmeasurable base — claims nothing, standard closing stands
    public void A_green_or_unmeasurable_baseline_keeps_the_standard_closing(bool baselinePassed, string baselineDetail)
    {
        var task = RealSupervisorActionExecutor.ApplyPriorFailureDiagnosis(Task_(),
            FailedPrior() with { BaselinePassed = baselinePassed, BaselineDetail = baselineDetail });

        task.Goal.ShouldContain("Fix what this output names, then make the check pass before finishing.");
        task.Goal.ShouldNotContain("pre-exists your work");
    }

    // ── Serialization: the tail is null-omitted (tape byte-parity) ──────────────────────

    [Fact]
    public void A_result_without_a_tail_serializes_byte_identical_to_pre_slice()
    {
        var result = new SupervisorAgentResult { AgentRunId = Guid.Empty, Status = "Succeeded", AcceptancePassed = false, AcceptanceDetail = "tests-failed-exit-1" };

        JsonSerializer.Serialize(result, AgentJson.Options).ShouldNotContain("acceptanceEvidenceTail", Case.Insensitive, "null-omitted — an ungraded/passed/pre-P5-2 row's durable bytes are unchanged (no idempotency-key drift)");
    }

    [Fact]
    public void A_result_with_a_tail_round_trips_it()
    {
        var json = JsonSerializer.Serialize(new SupervisorAgentResult { AgentRunId = Guid.Empty, Status = "Succeeded", AcceptanceEvidenceTail = "exit=1\nFAILED" }, AgentJson.Options);

        JsonSerializer.Deserialize<SupervisorAgentResult>(json, AgentJson.Options)!.AcceptanceEvidenceTail.ShouldBe("exit=1\nFAILED");
    }

    [Fact]
    public void A_grade_without_a_tail_serializes_byte_identical_to_pre_slice()
    {
        JsonSerializer.Serialize(new BenchmarkGrade { Passed = false, Detail = "d" }, AgentJson.Options)
            .ShouldNotContain("evidenceTail", Case.Insensitive, "null-omitted on the grade too — resolve/stop folds that serialize grades keep their bytes");
    }
}
