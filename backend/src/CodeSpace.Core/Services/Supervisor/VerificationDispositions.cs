using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The ONE mapping from today's lossy acceptance signals (a <c>bool?</c> verdict + a detail whose prefixes encode
/// infra-vs-genuine) onto the F0 <see cref="VerificationDisposition"/> — so the reducer (F0b PR-2), the manifest
/// write-back, and every future consumer classify identically instead of re-deriving prefix conventions. Anchored
/// on the SAME shared classifier every retry/evidence reader already uses
/// (<see cref="Agents.AgentAcceptanceContract.IsInfraFailure"/>).
/// </summary>
public static class VerificationDispositions
{
    /// <summary>Classify a grade's raw signals. <c>null</c> (never graded) → <see cref="VerificationDisposition.Unknown"/> — <see cref="VerificationDisposition.NotApplicable"/> is reserved for the vacuous-pass reclassification (a separate, metric-shifting PR).</summary>
    public static VerificationDisposition Classify(bool? acceptancePassed, string? detail, bool workPresent) => acceptancePassed switch
    {
        true => VerificationDisposition.Passed,
        false => Agents.AgentAcceptanceContract.IsInfraFailure(detail, workPresent) ? VerificationDisposition.InfraUnknown : VerificationDisposition.Failed,
        null => VerificationDisposition.Unknown,
    };

    /// <summary>
    /// Project the typed disposition onto the legacy 3-value manifest vocabulary — BYTE-COMPATIBLE with the
    /// executor's own historical <c>bool?</c> switch (an infra-classed false wrote <c>Failed</c> there too, so
    /// <see cref="VerificationDisposition.InfraUnknown"/> keeps projecting to <see cref="PublishAcceptanceState.Failed"/>
    /// until the typed field replaces the legacy one — reclassifying here would silently shift the
    /// delivery scorecard). Everything unmeasured/withheld projects to <see cref="PublishAcceptanceState.NotApplicable"/>.
    /// </summary>
    public static PublishAcceptanceState ToLegacyAcceptanceState(VerificationDisposition disposition) => disposition switch
    {
        VerificationDisposition.Passed => PublishAcceptanceState.Passed,
        VerificationDisposition.Failed or VerificationDisposition.InfraUnknown => PublishAcceptanceState.Failed,
        _ => PublishAcceptanceState.NotApplicable,
    };
}
