using CodeSpace.Messages.Contracts;

namespace CodeSpace.Core.Services.Completion;

/// <summary>
/// BOTH directions of the reason string a <see cref="TerminalDecision.HonestFailure"/> arbitration writes to
/// <c>workflow_run.error</c>: the ONE renderer <see cref="CompletionTerminalAuthority"/> stamps it with, and the ONE
/// reader that tells the arbiter's honest-failure arms apart afterwards. One shape, so a slot cannot be added
/// to the terminal without the reader seeing it — or read for a slot the terminal never wrote.
///
/// <para><b>Why the arms must be told apart.</b> <see cref="TerminalDecider.Decide"/> reaches HonestFailure three
/// ways: a disorderly <see cref="ExecutionDisposition.ForcedStop"/>/<see cref="ExecutionDisposition.Cancelled"/>
/// end, an <see cref="OutcomeDisposition.Unsolved"/> objective, and a Solved objective whose work failed to be
/// captured (<see cref="ArtifactDisposition.CaptureFailed"/>). They stamp the SAME prefix over two different
/// judgements — some of them judge the BRAIN, the rest judge the ENGINE or the harness around it — and a
/// real-model gate that reads only the PREFIX would stop gating all of them at once.</para>
///
/// <para><b>The arbiter judging the BRAIN</b> (a shortfall: reported, never gating).
/// <list type="bullet">
/// <item><c>execution=Completed</c> + <c>outcome=Unsolved</c> — the model worked the task to an orderly end and did
/// not solve it.</item>
/// <item><c>execution=ForcedStop</c> + <c>outcome=Unknown</c>/<c>Unsolved</c> — a supervisor BOUND tripped. Every
/// forced stop is the bound-keeper working as designed: <see cref="Supervisor.SupervisorBounds"/> returns one of the
/// <see cref="Supervisor.SupervisorStopReasons"/> literals (no-progress, the total-spawn cap, the cost cap, the
/// resolve budget), <c>SupervisorTurnService.GateForcedStop</c> stamps it, and
/// <see cref="CompletionReducer"/> turns any recorded reason into ForcedStop. The run exhausted a budget instead of
/// driving the arc — that is the brain not driving, not the engine failing to execute it. Real-model run
/// 34073320723's delivery-gate arm ended <c>(outcome=Unknown, verification=Unknown, artifact=Unknown,
/// execution=ForcedStop)</c> and was reported as an engine regression for it.</item>
/// </list></para>
///
/// <para><b>The arbiter judging the ENGINE or the harness</b> (gating — the losses the real-model lane exists to red
/// on).
/// <list type="bullet">
/// <item><c>artifact=CaptureFailed</c>, under ANY execution — the work existed and the capture/publish path dropped
/// it. It outranks the execution slot, so a forced stop over a capture failure still gates.</item>
/// <item><c>execution=Cancelled</c> — a real-model run cancelled mid-arc is the harness or the infrastructure, never
/// the model's doing.</item>
/// <item>A DECIDED objective cut off by a bound (<c>ForcedStop</c> over <c>Solved</c> or <c>Abstained</c>) — the
/// objective was settled, so nothing about it is a shortfall, and the pair is contradictory enough to want a human.
/// The read below is an ALLOWLIST of (execution, outcome) pairs, so it lands in the gating class rather than
/// guessing at one.</item>
/// </list></para>
///
/// <para><b>Why <c>execution</c> is in the rendering.</b> The forced-stop arm fires BEFORE the outcome switch
/// (<c>TerminalDecider:26</c>), so a ForcedStop over an Unsolved objective renders <c>outcome=Unsolved</c> too —
/// indistinguishable from the orderly-end shortfall, and from its Cancelled twin, until the execution disposition is
/// on the wire. It is the slot that separates the bound-keeper from the kill.</para>
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
    /// Whether this run error is the arbiter's honest failure over a BRAIN shortfall — the model reached an orderly
    /// end without solving the objective, or exhausted one of its bounds instead of driving the arc. Everything else
    /// about a <c>Failure</c> run is an engine fault to whoever asks: an unrecognised error, a reason merely QUOTING
    /// the prefix (matched at the START only — the marker-never-a-word rule), a cancelled run, a capture failure, and
    /// a decided objective a bound cut off.
    ///
    /// <para>Conservative in the gating direction by construction: the execution/outcome pair is an ALLOWLIST, so a
    /// reason written before a slot existed, or by a renderer that stopped writing one, reads as an engine fault
    /// rather than silently laundering itself into the non-gating class.</para>
    /// </summary>
    public static bool IsBrainShortfall(string? runError) =>
        runError is not null
        && runError.StartsWith(CompletionTerminalAuthority.HonestFailureReasonPrefix, StringComparison.Ordinal)
        && Slot(runError, ArtifactSlot) != nameof(ArtifactDisposition.CaptureFailed)
        && JudgesTheBrain(Slot(runError, ExecutionSlot), Slot(runError, OutcomeSlot));

    /// <summary>Whether the arm named by these two slots is the arbiter judging the BRAIN. An unrecognised execution value — or a null one, from a reason written before the slot existed — is nobody's shortfall.</summary>
    private static bool JudgesTheBrain(string? execution, string? outcome) => execution switch
    {
        nameof(ExecutionDisposition.Completed) => outcome == nameof(OutcomeDisposition.Unsolved),
        nameof(ExecutionDisposition.ForcedStop) => outcome is nameof(OutcomeDisposition.Unsolved) or nameof(OutcomeDisposition.Unknown),
        _ => false,
    };

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
