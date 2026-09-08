using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Review;

namespace CodeSpace.Core.Services.Review;

/// <summary>
/// The canonical <see cref="CriticRequest.ArtifactKind"/> vocabulary — a shared constant so a PRODUCER and a
/// kind-scoped CONSUMER (the ⑧ plan-satisfiability clause) agree on the EXACT wire string. Without it, the consumer
/// would match a fragile substring (a future kind like "explanation" contains "plan"); with it, a new kind is added
/// HERE and every site references it, so producer and guard can never silently drift apart (Rule 8 — pin the string).
/// </summary>
public static class CriticArtifactKinds
{
    public const string WorkflowPlan = "workflow plan";
    public const string SupervisorDecision = "supervisor decision";

    /// <summary>The S8/C1 output review's two artifact kinds — the only ones a <c>review.skipped</c> beat can carry and still be about a RESULT, never an intention (Rule 8 — the Room's fold pins the exact strings).</summary>
    public const string AgentChange = "agent change";
    public const string AgentAnswer = "agent answer";
}

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

    /// <summary>Compatibility producer row for callers without a full <see cref="ProducerModel"/>. New model-backed producers should carry the full identity so alias evidence can participate.</summary>
    public Guid? ProducerModelRowId { get; init; }

    /// <summary>The producer's routed and provider-observed identity. The row supports candidate selection; only the observed value supports an independence claim.</summary>
    public ReviewModelIdentity? ProducerModel { get; init; }

    /// <summary>
    /// The journal intent label this review's model call records under — null ⇒ the critic's own default
    /// (<c>critic.review</c>). The OUTPUT review names its own (<c>critic.output</c>) because it is the only rung that
    /// examines a RESULT: a plan critic and a decision critic examine an INTENTION, and a consumer asking "did anything
    /// check what this run produced?" must not be answered yes by either. Every kind is a const on the critic (Rule 8).
    /// </summary>
    public string? CallKind { get; init; }

    /// <summary>
    /// The AGENT RUN this review is ABOUT — set only by the OUTPUT review's two call sites (the model rung and the D②
    /// co-sign), mirroring <c>AgentRunExecutor.RecordOutputReviewVerdictAsync</c>'s own <c>agentRunId</c>. Without it, a
    /// <c>review.skipped</c> beat (which never otherwise names a unit) fell back to its ledger CELL while a
    /// <c>review.completed</c> beat for the SAME run grouped by this id — one reviewed unit read as two in
    /// <c>RoomProjector.FoldReviewVerdicts</c>, letting a stray skip outrank the run's own later verdict. A plan/decision
    /// review names no unit (unchanged) — there is no single agent run an intention review is "about".
    /// </summary>
    public Guid? AgentRunId { get; init; }
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
