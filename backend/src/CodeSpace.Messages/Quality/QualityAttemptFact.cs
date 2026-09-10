using CodeSpace.Messages.Contracts;

namespace CodeSpace.Messages.Quality;

/// <summary>
/// ONE already-made attempt, reduced to the single CLASSIFICATION the platform actually records for it (P22,
/// Rule 18.1 — a data noun): its typed verification verdict. Nothing else about the attempt enters the quality
/// surface — no model, no prompt, no diff, no output — so the policy can only ever read "what happened,
/// classified", never "who ran it".
///
/// <para><b>Why a verdict is the only member (and why a failure kind is NOT).</b> Carrying a <c>FailureKind</c>
/// here would make "the SAME failure happened again" decidable from a pair of enums — but nothing records one per
/// attempt. <c>FailureKind</c> is implemented only by exceptions (<c>IFailure.Kind</c>), while per-attempt
/// classification is <c>VerificationDispositions.Classify(acceptancePassed, detail, workPresent)</c>, whose entire
/// range is Passed / InfraUnknown / Failed / Unknown — the infra split coming from
/// <c>AgentAcceptanceContract.IsInfraFailure</c>'s detail prefixes. A member with no recorded source is an
/// invitation for each call site to invent its own, so there is none: infra-versus-work is carried by
/// <see cref="VerificationDisposition.InfraUnknown"/>, which <c>Classify</c> really does produce, and
/// <b>same-ness of failures is not decidable today</b>. The repeat evidence downstream is therefore "the check kept
/// failing", never "it kept failing the same way"; making the stronger claim available means first recording a
/// per-attempt failure classification, in its own PR.</para>
///
/// <para>One member is the honest width, not a wrapper waiting for a reason: this is the per-attempt fact row, and
/// a second recorded per-attempt classification becomes a second member HERE rather than a parallel list on
/// <see cref="QualityDecisionInput"/> that could drift out of step with this one.</para>
/// </summary>
public sealed record QualityAttemptFact
{
    /// <summary>The typed verification verdict recorded for this attempt. <see cref="VerificationDisposition.Unknown"/> = never graded; <see cref="VerificationDisposition.InfraUnknown"/> = the check machinery itself failed (NOT the work).</summary>
    public required VerificationDisposition Disposition { get; init; }
}
