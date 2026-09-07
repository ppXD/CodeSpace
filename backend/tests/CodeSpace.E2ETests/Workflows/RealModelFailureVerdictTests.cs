using CodeSpace.Core.Services.Completion;
using CodeSpace.IntegrationTests.Workflows.Supervisor;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// Pins the ONE rule every real-model arm applies to a run that ended <see cref="WorkflowRunStatus.Failure"/>: is
/// that a CODE FAULT (the engine could not execute the brain's decisions — gates the blessed wire) or a
/// CAPABILITY MISS (the engine worked exactly as designed and the brain fell short — reported, never gating)?
///
/// <para><b>Why it needs pinning.</b> Every arm used to answer "Failure ⇒ CodeFault", full stop. But the
/// completion terminal authority DELIBERATELY stamps Failure: when an Enforced-cohort run's contract is not
/// evidenced, <see cref="TerminalDecider"/> returns <see cref="TerminalDecision.HonestFailure"/> and the arbiter
/// overrides the engine's Success with <c>completion-authority: honest failure (…)</c>. That is the protocol
/// refusing to claim a success it cannot prove — the single most important thing it does. Main run 34068400279
/// reached it after the human adjudicated the patch-only conflict and the brain then re-planned itself into an
/// empty receipt set, and the delivery-gate arm reported "the engine FAULTED" over it, reddening the REQUIRED lane
/// for a capability shortfall.</para>
///
/// <para>Pure logic — no Postgres, no fixture, no model. Carries the E2E lane's traits only so it RUNS: the
/// <c>Category=E2E&amp;Surface=Engine</c> gate is the one CI lane that executes this assembly.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Engine")]
public sealed class RealModelFailureVerdictTests
{
    /// <summary>Verbatim shape of the arbiter's honest-failure verdict — as it reaches <c>workflow_run.error</c>.</summary>
    private const string HonestFailure = "completion-authority: honest failure (outcome=Unsolved, verification=Failed, artifact=Unknown)";

    /// <summary>A genuine engine death: an unhandled exception the engine folded into the run's error.</summary>
    private const string EngineFault = "Node failed.";

    [Fact]
    public void The_arbiters_designed_terminal_is_anchored_on_the_production_constant()
    {
        // Rule 8: the gates classify a run Failure by this prefix. If the arbiter's wording moves and this
        // constant does not, every honest-failure terminal starts reddening the REQUIRED real-model lane again.
        CompletionTerminalAuthority.HonestFailureReasonPrefix.ShouldBe("completion-authority: honest failure");
        HonestFailure.ShouldStartWith(CompletionTerminalAuthority.HonestFailureReasonPrefix);
    }

    // ── The shared rule ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(HonestFailure, RealModelOutcome.CapabilityMiss)]
    [InlineData(EngineFault, RealModelOutcome.CodeFault)]
    [InlineData(null, RealModelOutcome.CodeFault)]
    [InlineData("", RealModelOutcome.CodeFault)]
    [InlineData("the agent said: completion-authority: honest failure", RealModelOutcome.CodeFault)]
    public void A_failed_run_is_a_capability_miss_only_when_the_arbiter_authored_the_terminal(string? runError, RealModelOutcome expected)
    {
        RealModelGate.ClassifyRunFailure(runError).ShouldBe(expected,
            customMessage: "the verdict is read from the ARBITER's own leading slot — prose that merely quotes it is an engine fault like any other unrecognised error");
    }

    // ── The delivery-gate arm ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_delivery_gate_reports_an_adjudicated_honest_failure_without_gating()
    {
        var verdict = RealModelDeliveryGateE2ETests.FailedRunVerdict(HonestFailure, "after an adjudication answer");

        verdict.Outcome.ShouldBe(RealModelOutcome.CapabilityMiss, "run 34068400279's terminal: the brain re-planned into an empty receipt set and the arbiter refused to claim success — a miss, not a regression");
        verdict.Note.ShouldContain("honest failure", customMessage: "the note must quote the arbiter so a reader of the archived summary can tell the two apart");
        verdict.Note.ShouldNotContain("FAULTED");
    }

    [Fact]
    public void The_delivery_gate_still_gates_a_genuine_engine_fault()
    {
        var verdict = RealModelDeliveryGateE2ETests.FailedRunVerdict(EngineFault, "mid-arc");

        verdict.Outcome.ShouldBe(RealModelOutcome.CodeFault, "an unrecognised failure still reds — the honest rule narrows the fault class, it does not empty it");
        verdict.Note.ShouldContain(EngineFault);
    }

    // ── The whole-loop arm ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(WorkflowRunStatus.Failure, HonestFailure, false, RealModelOutcome.CapabilityMiss)]
    [InlineData(WorkflowRunStatus.Failure, EngineFault, false, RealModelOutcome.CodeFault)]
    [InlineData(WorkflowRunStatus.Failure, null, true, RealModelOutcome.CodeFault)]
    [InlineData(WorkflowRunStatus.Success, null, true, RealModelOutcome.Drove)]
    [InlineData(WorkflowRunStatus.Success, null, false, RealModelOutcome.CapabilityMiss)]
    public void The_whole_loop_arm_applies_the_same_rule(WorkflowRunStatus status, string? runError, bool drove, RealModelOutcome expected)
    {
        RealModelSupervisorWholeLoopE2ETests.ClassifyRun(status, runError, drove).ShouldBe(expected);
    }
}
