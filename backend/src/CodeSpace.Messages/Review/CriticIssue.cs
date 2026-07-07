namespace CodeSpace.Messages.Review;

/// <summary>
/// One concrete problem a reviewer found, WITH the evidence that grounds it AND how badly it undermines the artifact
/// (triad S8 + P1 severity — a data noun, Rule 18.1). Evidence turns an opinion into an auditable verdict; <see
/// cref="Severity"/> turns a flat "flagged" into a proportionate one — the gating axis. Renders as "text (evidence: …)"
/// UNCHANGED (severity drives the gate policy, not the rendered string), so every existing string consumer carries the
/// evidence exactly as before.
/// </summary>
public sealed record CriticIssue
{
    /// <summary>The problem, stated concretely.</summary>
    public required string Text { get; init; }

    /// <summary>The grounding — a quote or precise location in the artifact. Empty when the reviewer gave none (older cassettes / degraded reviews).</summary>
    public string Evidence { get; init; } = "";

    /// <summary>How badly this issue undermines the artifact — the gating axis (P1). The platform halts a gate on any <see cref="CriticSeverity.Blocker"/>; a <see cref="CriticSeverity.Minor"/> issue annotates without halting. Defaults to <see cref="CriticSeverity.Major"/> so an un-severitied issue (an older cassette, a degraded review) reads as a real-but-non-fatal concern — never a silent blocker, never a silent nitpick.</summary>
    public CriticSeverity Severity { get; init; } = CriticSeverity.Major;

    public override string ToString() => string.IsNullOrWhiteSpace(Evidence) ? Text : $"{Text} (evidence: {Evidence})";
}
