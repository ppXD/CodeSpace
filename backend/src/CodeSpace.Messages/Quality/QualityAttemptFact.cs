using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Failures;

namespace CodeSpace.Messages.Quality;

/// <summary>
/// ONE already-made attempt, reduced to the two CLASSIFICATIONS the platform already records for it (P22,
/// Rule 18.1 — a data noun): the typed verification verdict and, when the attempt failed, the domain failure
/// kind. Nothing else about the attempt enters the quality surface — no model, no prompt, no diff, no output —
/// so the policy can only ever read "what happened, classified", never "who ran it".
///
/// <para>Both members are ENUMS on purpose. The pair is what makes "the SAME failure happened again" decidable
/// without a failure message: two attempts are identical-for-policy iff both classifications match, so the
/// repeat-detection needs no string comparison and no fingerprint that could smuggle a path or a task name in
/// (see <see cref="QualityDecisionInput"/>'s purity invariant).</para>
/// </summary>
public sealed record QualityAttemptFact
{
    /// <summary>The typed verification verdict recorded for this attempt. <see cref="VerificationDisposition.Unknown"/> = never graded; <see cref="VerificationDisposition.InfraUnknown"/> = the check machinery itself failed (NOT the work).</summary>
    public required VerificationDisposition Disposition { get; init; }

    /// <summary>The domain failure kind recorded for this attempt, or null when it did not fail / nothing was classified.</summary>
    public FailureKind? Failure { get; init; }
}
