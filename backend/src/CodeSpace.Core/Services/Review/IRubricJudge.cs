using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Review;

namespace CodeSpace.Core.Services.Review;

/// <summary>
/// Judges a deliverable artifact against an <see cref="AcceptanceRubric"/> with an independent model — the
/// LLM-as-judge oracle backend (triad S7), a SIBLING of <see cref="IStructuredCritic"/> (Rule 7: a new capability is
/// a new narrow interface, never a widening). The judge answers each criterion with a BINARY met/not-met + evidence;
/// aggregation (weights, threshold) is the CALLER's — the grader owns the pass/fail math, the judge owns the reading.
/// NEVER throws (cancellation aside): any failure returns <see cref="RubricJudgeVerdict.JudgeFailed"/>, which the
/// grader maps to a fail-closed grade-error.
/// </summary>
public interface IRubricJudge
{
    Task<RubricJudgeVerdict> JudgeAsync(AcceptanceRubric rubric, string artifact, string? goal, Guid teamId, CancellationToken cancellationToken);
}
