using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Review;

/// <summary>
/// An independent reviewer model's verdict on a producer's output (Rule 18.1 — a data noun). The generic result of
/// <c>IStructuredCritic</c>, shared across producers (planner / supervisor / agent). <see cref="Failed"/> = the review
/// could not be produced (no reviewer model, a call/parse failure) — the caller FALLS BACK to the original output, so a
/// review is never WORSE than no review.
/// </summary>
public sealed record CriticVerdict
{
    /// <summary>The mode the review ran in (echoed for the caller's branch).</summary>
    public required ReviewMode Mode { get; init; }

    /// <summary>GATE: whether the reviewer approves the output. Always <c>false</c> on a failed review.</summary>
    public bool Approved { get; init; }

    /// <summary>An optional 0–100 quality score the reviewer assigned (GATE). Null when not scored / on failure.</summary>
    public int? Score { get; init; }

    /// <summary>Concrete issues the reviewer found (both modes), each carrying its EVIDENCE from the artifact (S8 — auditable verdicts, meta-evaluable reviews). Empty when none / on failure.</summary>
    public IReadOnlyList<CriticIssue> Issues { get; init; } = Array.Empty<CriticIssue>();

    /// <summary>IMPROVE: the critique text to fold BACK into the producer for its one revision. Null in GATE / on failure.</summary>
    public string? Critique { get; init; }

    /// <summary>The reviewer's rationale (never blank — degrades to a placeholder).</summary>
    public string Rationale { get; init; } = "";

    /// <summary>The review could not be produced (no reviewer model / a resolve, call, or parse failure) — the caller keeps the original output.</summary>
    public bool Failed { get; init; }

    /// <summary>A failed review — the caller falls back to the producer's original output.</summary>
    public static CriticVerdict ReviewFailed(ReviewMode mode, string reason) => new() { Mode = mode, Approved = false, Rationale = reason, Failed = true };
}
