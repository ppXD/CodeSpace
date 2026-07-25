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
/// </summary>
public static class SupervisorStopNowRecital
{
    /// <summary>The block's pinned header — a stable prompt landmark, mirroring <see cref="SupervisorBoundsRecitation.Header"/>.</summary>
    public const string Header = "IF YOU STOPPED NOW (the completion reducer's verdict on the facts so far):";

    /// <summary>Render the recital, or null when there is no assessment to recite (contract-less / pre-F0 run).</summary>
    public static string? Render(CompletionAssessment? assessment)
    {
        if (assessment is null) return null;

        var unresolved = new List<string>();

        if (assessment.Outcome != OutcomeDisposition.Solved) unresolved.Add($"outcome={assessment.Outcome}");
        if (assessment.Verification is not (VerificationDisposition.Passed or VerificationDisposition.NotApplicable)) unresolved.Add($"verification={assessment.Verification}");
        if (assessment.Artifact is not (ArtifactDisposition.Captured or ArtifactDisposition.NothingExpected)) unresolved.Add($"artifact={assessment.Artifact}");
        if (assessment.Delivery is not (DeliveryDisposition.Delivered or DeliveryDisposition.NotRequired)) unresolved.Add($"delivery={assessment.Delivery}");

        return unresolved.Count == 0
            ? $"{Header}\n- every contract dimension reads SETTLED — a clean stop now reads Solved. If the goal is met, stop rather than spending further turns on a contract that is already satisfied."
            : $"{Header}\n- UNRESOLVED: {string.Join(", ", unresolved)} — a stop right now cannot read Solved. Settle what is owed (make the failing checks pass, land the owed delivery/output), or stop honestly / ask a human — never stop as if done.";
    }
}
