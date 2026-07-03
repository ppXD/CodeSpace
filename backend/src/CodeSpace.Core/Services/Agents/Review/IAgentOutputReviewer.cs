using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Review;

namespace CodeSpace.Core.Services.Agents.Review;

/// <summary>
/// Reviews a producer run's output with a REAL, INDEPENDENT agent (triad S8 — the owner's "true adversarial agent"
/// ask): a read-only agent run that CLONES the produced branch and inspects the actual repository — it can grep,
/// read neighbours, and follow the change — rather than judging a diff string. A SIBLING of the in-process
/// <c>IStructuredCritic</c> (Rule 7); the executor falls back to the model critic when this reviewer cannot
/// produce a verdict, so an agent review is never worse than a model review.
/// </summary>
public interface IAgentOutputReviewer
{
    /// <summary>Run one independent review agent over <paramref name="result"/>'s produced branch and return its verdict. NEVER throws (cancellation aside) — any failure returns <c>CriticVerdict.ReviewFailed</c> so the caller can ladder down to the model critic.</summary>
    Task<CriticVerdict> ReviewAsync(AgentTask producerTask, AgentRunResult result, AgentRun run, CancellationToken cancellationToken);
}
