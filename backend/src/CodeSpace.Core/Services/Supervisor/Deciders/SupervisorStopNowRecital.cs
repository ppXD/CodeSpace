using CodeSpace.Messages.Agents;
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
///
/// <para>That line is MODE-AWARE for the same reason. <c>CompletionTerminalAuthority</c> refuses nothing outside
/// <see cref="CompletionEnforcementMode.Enforced"/> (its first line passes a Legacy/Shadow run through verbatim).
/// Since #1774 (C5), <see cref="Completion.CompletionPolicy.DefaultModeFor"/> stamps the supervisor profile — the
/// only <see cref="ProtocolReadiness.Enforceable"/> one — <see cref="CompletionEnforcementMode.Enforced"/> BY
/// DEFAULT; <c>CompletionPolicy.CurrentMode</c> (Shadow) is now only the fallback constant <c>DefaultModeFor</c>
/// returns for the below-the-bar cohorts (plan-map, single-agent, an unregistered generic graph). A supervisor run
/// can still carry a non-Enforced stamp — an explicit 'shadow' opt-in, or tape stamped before the lane graduated —
/// and that run would NOT be refused by the terminal authority, so a recital that hard-coded REFUSED would steer
/// it away from a stop the engine would have honoured. Enforced says REFUSED; every other mode states the SAME
/// facts as an advisory. The verb never renders outside Enforced.</para>
/// </summary>
public static class SupervisorStopNowRecital
{
    /// <summary>The block's pinned header — a stable prompt landmark, mirroring <see cref="SupervisorBoundsRecitation.Header"/>.</summary>
    public const string Header = "IF YOU STOPPED NOW (the completion reducer's verdict on the facts so far):";

    /// <summary>The stage line's pinned lead-in under <see cref="CompletionEnforcementMode.Enforced"/>, where the authority really does refuse — a stable prompt landmark, like <see cref="Header"/>.</summary>
    public const string RefusalLead = "A stop now will be REFUSED by the completion authority:";

    /// <summary>The same line's lead-in under every OTHER mode, where the stop is honoured but recorded against evidence the profile declares owed. Same facts, same steer, no refusal verb.</summary>
    public const string AdvisoryLead = "A 'completed' stop now would be recorded against missing evidence:";

    /// <summary>
    /// The stage line's shared steer. It names the HONEST EXIT beside the work-it-off option, because landing the
    /// work is not always available: past <c>SupervisorLane.DefaultMaxResolveAttempts</c> (one) a further
    /// <c>resolve</c> force-stops the run, and a prompt offering only "land it or ask_human" then reads as a dead
    /// end. A <c>stop</c> carrying outcome <c>gave_up</c> is NOT refused by this gate — it reduces to Unsolved,
    /// which <c>TerminalDecider</c> maps to HonestFailure long before the stage gate, which only ever sees a
    /// CleanSuccess. Only the <c>completed</c> claim is what this block is warning about.
    ///
    /// <para>The LANDING half is state-dependent, and that is the whole point of this function. Naming a landing
    /// verb the tape cannot reach is what sent run 34027621996 into a blind re-merge: a conflicted integration was
    /// recorded, the resolve cap was spent, and this line still said "Land that work" while <c>merge</c> was the
    /// only landing reading left — a merge that just repeats the same conflicted integration. The honest exits are
    /// the constant part; the landing clause is whatever <see cref="SupervisorActionMask.LandingReachFor"/> says is
    /// actually reachable, off the SAME facts the mask masks <c>resolve</c> on three lines below.</para>
    /// </summary>
    internal static string SteerFor(SupervisorLandingReach reach) => reach switch
    {
        SupervisorLandingReach.ReconcileFirst => $"Resolve the recorded conflict, {HonestExits}",
        SupervisorLandingReach.NoLandingReachable => $"Stop with outcome 'gave_up', or ask_human; {DoNotClaim}",
        _ => $"Land that work, {HonestExits}",
    };

    /// <summary>The constant half — the exits that stay open on every tape. A <c>stop</c> carrying <c>gave_up</c> is never refused by this gate, so it costs nothing to offer and it is the one move that always exists.</summary>
    private const string HonestExits = "stop with outcome 'gave_up', or ask_human; " + DoNotClaim;

    private const string DoNotClaim = "do not claim completed.";

    /// <summary>Render the recital, or null when there is no assessment to recite (contract-less / pre-F0 run). The stage trace, profile and enforcement mode are the terminal authority's own three inputs; omitting them (a tape mirror with no stage trace, an unregistered mode) renders the dimensions alone, byte-identically. <paramref name="notApplicableUpstream"/> is the authority's fourth input — the stage this run's repository policy put out of reach — and renders as a FACT, never a warning: the model must not be steered to "land that work" when no answer from inside the run could. <paramref name="landingReach"/> answers that same question for the run's own tape rather than its repository policy (<see cref="SupervisorActionMask.LandingReachFor"/>); its default leaves every pre-existing call site byte-identical.</summary>
    public static string? Render(CompletionAssessment? assessment, IReadOnlySet<CompletionStage>? exercisedUpstreamStages = null, ModeProfile? profile = null, CompletionEnforcementMode enforcementMode = CompletionEnforcementMode.Legacy, UpstreamStageNotApplicable? notApplicableUpstream = null, SupervisorLandingReach landingReach = SupervisorLandingReach.Unconstrained)
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

        return $"{Header}\n{verdict}{StageNote(notApplicableUpstream, profile)}{StageRefusal(exercisedUpstreamStages, profile, enforcementMode, notApplicableUpstream, landingReach)}";
    }

    /// <summary>
    /// The mode profile's Required upstream stages this run's evidence does NOT show — the objection
    /// <c>CompletionTerminalAuthority</c> would raise against a <c>completed</c> stop taken right now, rendered
    /// BEFORE the stop is chosen. Deliberately renders in BOTH arms above: the settled arm is exactly where the gap
    /// bit, since a contract can read wholly settled while an un-reconciled branch leaves Integrate unevidenced.
    /// Empty (no missing stage, or a mode with no registered profile — the authority parks such a run Unsupported
    /// on its own gate, which this block has nothing to add to) renders nothing at all.
    ///
    /// <para>The FACTS are identical in both modes — the same profile, count and stage list, from the same reader.
    /// Only the lead-in moves, because only an Enforced run can actually be refused
    /// (<c>CompletionTerminalAuthority.cs:59</c>). The STEER is identical in both modes too, for the same reason
    /// in reverse: which verbs a tape leaves reachable is a fact about the run, not about the cohort that grades
    /// it, so the mode picks the lead word and nothing else.</para>
    /// </summary>
    private static string StageRefusal(IReadOnlySet<CompletionStage>? exercisedUpstreamStages, ModeProfile? profile, CompletionEnforcementMode enforcementMode, UpstreamStageNotApplicable? notApplicableUpstream, SupervisorLandingReach landingReach)
    {
        if (profile is null) return string.Empty;

        var missing = Completion.UpstreamStageTrace.MissingRequired(profile, exercisedUpstreamStages, notApplicableUpstream);

        if (missing.Count == 0) return string.Empty;

        var lead = enforcementMode == CompletionEnforcementMode.Enforced ? RefusalLead : AdvisoryLead;

        return $"\n- {lead} mode '{profile.Mode}' requires {missing.Count} stage(s) with no evidence — {string.Join(", ", missing)}. {SteerFor(landingReach)}";
    }

    /// <summary>
    /// The stage the run's own repository policy put OUT OF REACH, stated as a settled fact. It renders BESIDE
    /// the refusal line, never instead of it: other stages can still be genuinely missing, and a model told only
    /// "integration is not applicable" would read that as permission to stop. Silent when nothing is
    /// policy-bounded, so every run without one renders byte-identically to before.
    ///
    /// <para>Sentence authored HERE and nowhere else — <see cref="UpstreamStageNotApplicable.Reason"/> is the
    /// backend's own words, which the Room prints verbatim too, so the prompt and the operator's view of the same
    /// run cannot describe it differently.</para>
    /// </summary>
    private static string StageNote(UpstreamStageNotApplicable? notApplicableUpstream, ModeProfile? profile) =>
        profile is null || notApplicableUpstream is null ? string.Empty : $"\n- {notApplicableUpstream.Reason}.";
}
