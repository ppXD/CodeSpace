using CodeSpace.Messages.Contracts;

namespace CodeSpace.Core.Services.Supervisor.Deciders;

/// <summary>
/// P5-6: render the reducer's OWN verdict on "what if you stopped cleanly right now" for the decider prompt —
/// the mid-run mirror of the terminal assessment, so the perception gap behind stop-without-shipping closes
/// BEFORE the stop is chosen instead of surfacing as a degraded terminal afterwards. Dimensions that already
/// read settled-positive are omitted (the #1256 session-recital convention); Execution is skipped (the run is
/// mid-flight by definition). Both directions render: unresolved dimensions steer to settling them (or an honest
/// stop/ask — never a fake-done stop), and an all-clear steers to stopping rather than spending more turns on a
/// contract that is already met. Pure over the assessment; the DB-reading compose happens at rehydrate.
///
/// <para>The four contract dimensions are not the whole refusal surface: a Success claim ALSO has to evidence every
/// upstream stage the run's mode declares Required, and <c>CompletionTerminalAuthority</c> parks the run naming the
/// stage(s) it cannot see. A prompt that recited only the dimensions could say "every contract dimension reads
/// SETTLED" while the stop it was steering toward was already guaranteed to be refused — the run stopped
/// <c>completed</c> over an un-reconciled conflict and parked on "requires stage(s) with no evidence: Integrate"
/// (real-model runs 33930904059 / 33943475246). So the stage line renders too, read through the authority's OWN
/// reader (<see cref="Completion.UpstreamStageTrace.MissingRequired"/>) — never a second derivation that could
/// disagree with the gate it is mirroring.</para>
/// </summary>
public static class SupervisorStopNowRecital
{
    /// <summary>The block's pinned header — a stable prompt landmark, mirroring <see cref="SupervisorBoundsRecitation.Header"/>.</summary>
    public const string Header = "IF YOU STOPPED NOW (the completion reducer's verdict on the facts so far):";

    /// <summary>The stage-refusal line's pinned lead-in — a stable prompt landmark, like <see cref="Header"/>.</summary>
    public const string RefusalLead = "A stop now will be REFUSED by the completion authority:";

    /// <summary>Render the recital, or null when there is no assessment to recite (contract-less / pre-F0 run). The stage trace and profile are the terminal authority's own two inputs; omitting them (a tape mirror with no stage trace, an unregistered mode) renders the dimensions alone, byte-identically.</summary>
    public static string? Render(CompletionAssessment? assessment, IReadOnlySet<CompletionStage>? exercisedUpstreamStages = null, ModeProfile? profile = null)
    {
        if (assessment is null) return null;

        var unresolved = new List<string>();

        if (assessment.Outcome != OutcomeDisposition.Solved) unresolved.Add($"outcome={assessment.Outcome}");
        if (assessment.Verification is not (VerificationDisposition.Passed or VerificationDisposition.NotApplicable)) unresolved.Add($"verification={assessment.Verification}");
        if (assessment.Artifact is not (ArtifactDisposition.Captured or ArtifactDisposition.NothingExpected)) unresolved.Add($"artifact={assessment.Artifact}");
        if (assessment.Delivery is not (DeliveryDisposition.Delivered or DeliveryDisposition.NotRequired)) unresolved.Add($"delivery={assessment.Delivery}");

        var verdict = unresolved.Count == 0
            ? "- every contract dimension reads SETTLED — a clean stop now reads Solved. If the goal is met, stop rather than spending further turns on a contract that is already satisfied."
            : $"- UNRESOLVED: {string.Join(", ", unresolved)} — a stop right now cannot read Solved. Settle what is owed (make the failing checks pass, land the owed delivery/output), or stop honestly / ask a human — never stop as if done.";

        return $"{Header}\n{verdict}{StageRefusal(exercisedUpstreamStages, profile)}";
    }

    /// <summary>
    /// The mode profile's Required upstream stages this run's evidence does NOT show — the refusal
    /// <c>CompletionTerminalAuthority</c> would raise against a stop taken right now, rendered BEFORE the stop is
    /// chosen. Deliberately renders in BOTH arms above: the settled arm is exactly where the gap bit, since a
    /// contract can read wholly settled while an un-reconciled branch leaves Integrate unevidenced. Empty (no
    /// missing stage, or a mode with no registered profile — the authority parks such a run Unsupported on its own
    /// gate, which this block has nothing to add to) renders nothing at all.
    /// </summary>
    private static string StageRefusal(IReadOnlySet<CompletionStage>? exercisedUpstreamStages, ModeProfile? profile)
    {
        if (profile is null) return string.Empty;

        var missing = Completion.UpstreamStageTrace.MissingRequired(profile, exercisedUpstreamStages);

        return missing.Count == 0
            ? string.Empty
            : $"\n- {RefusalLead} mode '{profile.Mode}' requires {missing.Count} stage(s) with no evidence — {string.Join(", ", missing)}. Land that work or ask_human; do not claim completed.";
    }
}
