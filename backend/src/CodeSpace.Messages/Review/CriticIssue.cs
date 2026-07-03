namespace CodeSpace.Messages.Review;

/// <summary>
/// One concrete problem a reviewer found, WITH the evidence that grounds it (triad S8 — a data noun, Rule 18.1).
/// Evidence is what turns an opinion into an auditable verdict: a quote or precise location IN the artifact that a
/// human (or a meta-evaluation of the reviewer) can check without re-reading everything. Renders as
/// "text (evidence: …)" so every existing string consumer (timeline warnings, revise instructions, risk
/// annotations) carries the evidence for free.
/// </summary>
public sealed record CriticIssue
{
    /// <summary>The problem, stated concretely.</summary>
    public required string Text { get; init; }

    /// <summary>The grounding — a quote or precise location in the artifact. Empty when the reviewer gave none (older cassettes / degraded reviews).</summary>
    public string Evidence { get; init; } = "";

    public override string ToString() => string.IsNullOrWhiteSpace(Evidence) ? Text : $"{Text} (evidence: {Evidence})";
}
