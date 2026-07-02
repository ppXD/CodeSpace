using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Review;

namespace CodeSpace.Core.Services.Review;

/// <summary>What an <see cref="IStructuredCritic"/> reviews (Rule 18.1 — a data noun): the producer's output rendered as text, the kind of artifact, the mode, and the goal it should serve.</summary>
public sealed record CriticRequest
{
    /// <summary><see cref="ReviewMode.Gate"/> or <see cref="ReviewMode.Improve"/> (never <see cref="ReviewMode.None"/> — the caller short-circuits None before reviewing).</summary>
    public required ReviewMode Mode { get; init; }

    /// <summary>What the artifact IS, for the prompt (e.g. "workflow plan", "supervisor decision", "agent change").</summary>
    public required string ArtifactKind { get; init; }

    /// <summary>The producer's output rendered as text for the reviewer to judge.</summary>
    public required string Artifact { get; init; }

    /// <summary>The goal / task the artifact should serve — the yardstick the reviewer judges against. Optional.</summary>
    public string? Goal { get; init; }

    /// <summary>The PRODUCER's credentialed-model row (when the caller knows it) — the auto reviewer pick prefers a DIFFERENT model for a real second opinion, falling back to this same model on a one-model pool. Null ⇒ no preference (today's pick).</summary>
    public Guid? ProducerModelRowId { get; init; }
}

/// <summary>
/// The generic ADVERSARIAL-REVIEW primitive — an INDEPENDENT model reviews a producer's output, in one of two modes:
/// GATE (score / approve + surface issues) or IMPROVE (critique to fold back for a revision). The "send my plan to
/// another model and combine the critique" pattern, generalized + reusable across producers (the planner first; the
/// supervisor decision + the agent output later) — Rule 7: a new producer reuses THIS, not a bespoke critic.
///
/// <para>Mirrors <c>LlmDecisionArbiter</c>'s independent-brain call EXACTLY (resolve a model row → match the structured
/// client by ITS provider → schema-constrained completion) and FAILS CLOSED to a <see cref="CriticVerdict.Failed"/>
/// verdict: a missing / unusable reviewer model, no structured provider, or a malformed review all return a failed
/// verdict so the CALLER keeps the producer's original output — a review is never worse than no review. NEVER throws
/// (cancellation aside).</para>
/// </summary>
public interface IStructuredCritic
{
    /// <summary>Review <paramref name="request"/> with an INDEPENDENT model — the operator-pinned <paramref name="reviewerModelId"/> when set, else the team's auto-picked brain (the strongest structured-eligible pool model). Returns a <see cref="CriticVerdict"/>; a failed review returns <see cref="CriticVerdict.Failed"/> (the caller falls back).</summary>
    Task<CriticVerdict> ReviewAsync(CriticRequest request, Guid teamId, Guid? reviewerModelId, CancellationToken cancellationToken);
}
