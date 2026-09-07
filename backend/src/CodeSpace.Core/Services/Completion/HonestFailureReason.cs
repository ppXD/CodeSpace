using CodeSpace.Messages.Contracts;

namespace CodeSpace.Core.Services.Completion;

/// <summary>
/// BOTH directions of the reason string a <see cref="TerminalDecision.HonestFailure"/> arbitration writes to
/// <c>workflow_run.error</c>: the ONE renderer <see cref="CompletionTerminalAuthority"/> stamps it with, and the ONE
/// reader that tells the arbiter's THREE honest-failure arms apart afterwards. One shape, so a slot cannot be added
/// to the terminal without the reader seeing it — or read for a slot the terminal never wrote.
///
/// <para><b>Why the arms must be told apart.</b> <see cref="TerminalDecider.Decide"/> reaches HonestFailure three
/// ways: a disorderly <see cref="ExecutionDisposition.ForcedStop"/>/<see cref="ExecutionDisposition.Cancelled"/>
/// end, an <see cref="OutcomeDisposition.Unsolved"/> objective, and a Solved objective whose work failed to be
/// captured (<see cref="ArtifactDisposition.CaptureFailed"/>). Only the MIDDLE one is a brain shortfall — the model
/// worked the task to an orderly end and did not solve it. The other two are engine-side regressions: a run killed
/// mid-flight, or a capture/publish path that dropped the work. A real-model gate that reads only the PREFIX would
/// stop gating all three, and a capture regression would go quiet on the one lane built to catch it.</para>
///
/// <para><b>Why <c>execution</c> is in the rendering.</b> The forced-stop arm fires BEFORE the outcome switch, so a
/// ForcedStop over an Unsolved objective renders <c>outcome=Unsolved</c> too — indistinguishable from the brain
/// shortfall until the execution disposition is on the wire. Reaching the outcome switch at all requires
/// <see cref="ExecutionDisposition.Completed"/>, which is what <see cref="IsBrainShortfall"/> positively requires.</para>
/// </summary>
public static class HonestFailureReason
{
    private const string OutcomeSlot = "outcome";
    private const string VerificationSlot = "verification";
    private const string ArtifactSlot = "artifact";
    private const string ExecutionSlot = "execution";

    /// <summary>The reason the arbiter stamps: the pinned prefix (<see cref="CompletionTerminalAuthority.HonestFailureReasonPrefix"/>) followed by every disposition an arm of <see cref="TerminalDecider.Decide"/> can reach HonestFailure by, so the terminal names WHICH arm fired.</summary>
    public static string Render(CompletionAssessment assessment) =>
        $"{CompletionTerminalAuthority.HonestFailureReasonPrefix} ({OutcomeSlot}={assessment.Outcome}, {VerificationSlot}={assessment.Verification}, {ArtifactSlot}={assessment.Artifact}, {ExecutionSlot}={assessment.Execution})";

    /// <summary>
    /// Whether this run error is the arbiter's honest failure over a BRAIN shortfall — the run reached an orderly end
    /// and the objective was not solved. Everything else about a <c>Failure</c> run is an engine fault to whoever asks:
    /// an unrecognised error, a reason merely QUOTING the prefix (matched at the START only — the marker-never-a-word
    /// rule), a killed or cancelled run, and a capture failure.
    ///
    /// <para>Conservative in the gating direction by construction: every slot is required POSITIVELY, so a reason
    /// written before a slot existed, or by a renderer that stopped writing one, reads as an engine fault rather than
    /// silently laundering itself into the non-gating class.</para>
    /// </summary>
    public static bool IsBrainShortfall(string? runError) =>
        runError is not null
        && runError.StartsWith(CompletionTerminalAuthority.HonestFailureReasonPrefix, StringComparison.Ordinal)
        && Slot(runError, ExecutionSlot) == nameof(ExecutionDisposition.Completed)
        && Slot(runError, OutcomeSlot) == nameof(OutcomeDisposition.Unsolved)
        && Slot(runError, ArtifactSlot) != nameof(ArtifactDisposition.CaptureFailed);

    /// <summary>The value <see cref="Render"/> wrote for one slot, or null when the reason carries no such slot.</summary>
    private static string? Slot(string reason, string slot)
    {
        var at = reason.IndexOf($"{slot}=", StringComparison.Ordinal);

        if (at < 0) return null;

        var from = at + slot.Length + 1;
        var to = reason.IndexOfAny([',', ')'], from);

        return to < 0 ? reason[from..] : reason[from..to];
    }
}
