using CodeSpace.Core.Services.Completion;
using CodeSpace.IntegrationTests.Workflows.Supervisor;
using CodeSpace.Messages.Contracts;
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
/// <para><b>And why the PREFIX alone is not the rule.</b> <see cref="TerminalDecider"/> stamps that same prefix over
/// arms that judge two different things. It judges the BRAIN on an orderly end with the objective unsolved, and on a
/// FORCED STOP — a run that exhausted a supervisor bound (no-progress, the spawn cap, the cost cap) instead of
/// driving the arc, which is the bound-keeper working, not the engine failing. It judges the ENGINE or the harness
/// on a CANCELLED run and on work that was solved but failed to be CAPTURED — engine-side losses this lane exists to
/// red on. Reading only the prefix would have made a capture regression stop gating the moment this rule landed, so
/// the rule reads the arbiter's own dispositions (<see cref="HonestFailureReason.IsBrainShortfall"/>) and the
/// fixtures below are re-derived from the renderer that writes them.</para>
///
/// <para>Pure logic — no Postgres, no fixture, no model. Carries the E2E lane's traits only so it RUNS: the
/// <c>Category=E2E&amp;Surface=Engine</c> gate is the one CI lane that executes this assembly.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Engine")]
public sealed class RealModelFailureVerdictTests
{
    /// <summary>Verbatim shape of the arbiter's honest-failure verdict over a BRAIN shortfall — the run reached an orderly end and did not solve the objective. Pinned against the production renderer below.</summary>
    private const string HonestFailure = "completion-authority: honest failure (outcome=Unsolved, verification=Failed, artifact=Unknown, execution=Completed)";

    /// <summary>The SAME prefix over a capture regression: the objective WAS solved and the produced work failed to be captured (<c>TerminalDecider:39</c>). An engine-side loss — it must keep gating.</summary>
    private const string CaptureFailed = "completion-authority: honest failure (outcome=Solved, verification=Passed, artifact=CaptureFailed, execution=Completed)";

    /// <summary>The SAME prefix over a run whose supervisor bound tripped (<c>TerminalDecider:26</c>), and byte-identical to <see cref="HonestFailure"/> in every slot but <c>execution</c>. The brain did not drive the arc inside its budget — a shortfall, like the orderly-end one.</summary>
    private const string ForcedStop = "completion-authority: honest failure (outcome=Unsolved, verification=Failed, artifact=Unknown, execution=ForcedStop)";

    /// <summary>Real-model run 34073320723's delivery-gate arm, verbatim: a forced stop whose objective was never even statable. The bound-keeper cut a brain that had not driven the arc — a shortfall, and the terminal that made round 2's rule red this lane.</summary>
    private const string ForcedStopUnknown = "completion-authority: honest failure (outcome=Unknown, verification=Unknown, artifact=Unknown, execution=ForcedStop)";

    /// <summary>A capture regression hiding behind an otherwise-shortfall terminal: byte-identical to <see cref="HonestFailure"/> in every slot but <c>artifact</c>. The objective was unsolved AND the produced work was lost — the loss outranks the shortfall, so it gates.</summary>
    private const string CaptureFailedUnsolved = "completion-authority: honest failure (outcome=Unsolved, verification=Failed, artifact=CaptureFailed, execution=Completed)";

    /// <summary>The same regression hiding behind a BOUND: byte-identical to <see cref="ForcedStopUnknown"/> in every slot but <c>artifact</c>. This is the pair that makes the artifact conjunct load-bearing on the forced-stop arm — without it, a capture loss under a tripped bound would ride the non-gating class.</summary>
    private const string ForcedStopCaptureFailed = "completion-authority: honest failure (outcome=Unknown, verification=Unknown, artifact=CaptureFailed, execution=ForcedStop)";

    /// <summary>A bound that cut off an objective already DECIDED Solved. Nothing about a settled objective is a brain shortfall, and the pair is contradictory enough to want a human — the allowlist leaves it gating rather than guessing.</summary>
    private const string ForcedStopSolved = "completion-authority: honest failure (outcome=Solved, verification=Passed, artifact=Captured, execution=ForcedStop)";

    /// <summary>The cancellation twin of <see cref="ForcedStop"/> — the other half of the same decider arm, and the half that is NOT the brain's doing: a real-model run killed mid-arc is the harness or the infrastructure.</summary>
    private const string Cancelled = "completion-authority: honest failure (outcome=Unsolved, verification=Failed, artifact=Unknown, execution=Cancelled)";

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

    /// <summary>
    /// Rule 12.5 drift detector: the fixtures below are <c>const</c> because <c>[InlineData]</c> demands it, which
    /// makes them an INLINE MIRROR of a production string. Every one is re-derived here from the real renderer, so a
    /// slot added to (or dropped from) the terminal reds THIS test instead of quietly changing what the arms below
    /// are actually asserting about.
    /// </summary>
    [Theory]
    [InlineData(HonestFailure, ExecutionDisposition.Completed, OutcomeDisposition.Unsolved, VerificationDisposition.Failed, ArtifactDisposition.Unknown)]
    [InlineData(CaptureFailed, ExecutionDisposition.Completed, OutcomeDisposition.Solved, VerificationDisposition.Passed, ArtifactDisposition.CaptureFailed)]
    [InlineData(ForcedStop, ExecutionDisposition.ForcedStop, OutcomeDisposition.Unsolved, VerificationDisposition.Failed, ArtifactDisposition.Unknown)]
    [InlineData(ForcedStopUnknown, ExecutionDisposition.ForcedStop, OutcomeDisposition.Unknown, VerificationDisposition.Unknown, ArtifactDisposition.Unknown)]
    [InlineData(CaptureFailedUnsolved, ExecutionDisposition.Completed, OutcomeDisposition.Unsolved, VerificationDisposition.Failed, ArtifactDisposition.CaptureFailed)]
    [InlineData(ForcedStopCaptureFailed, ExecutionDisposition.ForcedStop, OutcomeDisposition.Unknown, VerificationDisposition.Unknown, ArtifactDisposition.CaptureFailed)]
    [InlineData(ForcedStopSolved, ExecutionDisposition.ForcedStop, OutcomeDisposition.Solved, VerificationDisposition.Passed, ArtifactDisposition.Captured)]
    [InlineData(Cancelled, ExecutionDisposition.Cancelled, OutcomeDisposition.Unsolved, VerificationDisposition.Failed, ArtifactDisposition.Unknown)]
    public void Every_fixture_is_what_the_arbiter_would_really_write(string fixture, ExecutionDisposition execution, OutcomeDisposition outcome, VerificationDisposition verification, ArtifactDisposition artifact)
    {
        var assessment = new CompletionAssessment
        {
            Basis = CompletionBasis.ContractDerived,
            Execution = execution,
            Outcome = outcome,
            Verification = verification,
            Artifact = artifact,
            Delivery = DeliveryDisposition.Unknown,
        };

        HonestFailureReason.Render(assessment).ShouldBe(fixture,
            customMessage: "the fixture is a mirror of the terminal the authority stamps — re-derive it here or the arms below stop testing the real string");
    }

    // ── The shared rule ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(HonestFailure, RealModelOutcome.CapabilityMiss)]
    [InlineData(EngineFault, RealModelOutcome.CodeFault)]
    [InlineData(null, RealModelOutcome.CodeFault)]
    [InlineData("", RealModelOutcome.CodeFault)]
    [InlineData("the agent said: completion-authority: honest failure", RealModelOutcome.CodeFault)]
    // A forced stop is a supervisor BOUND tripping — the no-progress, spawn, cost or resolve budget the run was
    // given. The bound-keeper working means the brain did not drive the arc, which is a shortfall like any other.
    // Real-model run 34073320723 ended exactly on `ForcedStopUnknown` and reddened this REQUIRED lane for it.
    [InlineData(ForcedStop, RealModelOutcome.CapabilityMiss)]
    [InlineData(ForcedStopUnknown, RealModelOutcome.CapabilityMiss)]
    // The arms that judge the ENGINE or the harness keep gating, or the lane goes quiet on exactly the losses it
    // exists to catch: work lost in capture (under ANY execution), and a run cancelled mid-arc.
    [InlineData(CaptureFailed, RealModelOutcome.CodeFault)]
    [InlineData(CaptureFailedUnsolved, RealModelOutcome.CodeFault)]
    [InlineData(ForcedStopCaptureFailed, RealModelOutcome.CodeFault)]
    [InlineData(Cancelled, RealModelOutcome.CodeFault)]
    // A bound that cut off an objective already DECIDED Solved is nobody's shortfall — the allowlist leaves the
    // contradictory pair in the gating class rather than guessing at it.
    [InlineData(ForcedStopSolved, RealModelOutcome.CodeFault)]
    // A pre-slot reason (a renderer that stopped writing `execution`) is unrecognised, never laundered.
    [InlineData("completion-authority: honest failure (outcome=Unsolved, verification=Failed, artifact=Unknown)", RealModelOutcome.CodeFault)]
    public void A_failed_run_is_a_capability_miss_only_when_the_arbiter_authored_the_terminal(string? runError, RealModelOutcome expected)
    {
        RealModelGate.ClassifyRunFailure(runError).ShouldBe(expected,
            customMessage: "the verdict is read from the ARBITER's own slots — the prefix spans arms that judge the brain (an orderly end unsolved, a bound that tripped) and arms that judge the engine (a cancellation, a capture failure); prose that merely quotes it is an engine fault like any other unrecognised error");
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
    public void The_delivery_gate_reports_a_bound_that_tripped_without_gating()
    {
        var verdict = RealModelDeliveryGateE2ETests.FailedRunVerdict(ForcedStopUnknown, "before the delivery conflict was adjudicated");

        verdict.Outcome.ShouldBe(RealModelOutcome.CapabilityMiss, "run 34073320723's terminal: the brain exhausted a supervisor bound instead of driving the arc — a miss, not a regression");
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
    [InlineData(WorkflowRunStatus.Failure, ForcedStopUnknown, false, RealModelOutcome.CapabilityMiss)]
    [InlineData(WorkflowRunStatus.Failure, Cancelled, false, RealModelOutcome.CodeFault)]
    [InlineData(WorkflowRunStatus.Failure, EngineFault, false, RealModelOutcome.CodeFault)]
    [InlineData(WorkflowRunStatus.Failure, null, true, RealModelOutcome.CodeFault)]
    [InlineData(WorkflowRunStatus.Success, null, true, RealModelOutcome.Drove)]
    [InlineData(WorkflowRunStatus.Success, null, false, RealModelOutcome.CapabilityMiss)]
    public void The_whole_loop_arm_applies_the_same_rule(WorkflowRunStatus status, string? runError, bool drove, RealModelOutcome expected)
    {
        RealModelSupervisorWholeLoopE2ETests.ClassifyRun(status, runError, drove).ShouldBe(expected);
    }
}
