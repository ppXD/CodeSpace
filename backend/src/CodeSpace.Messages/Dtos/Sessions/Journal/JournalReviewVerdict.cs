namespace CodeSpace.Messages.Dtos.Sessions.Journal;

/// <summary>
/// An independent reviewer's VERDICT, render-ready for the journal — the outcome of a real reviewer agent run (an
/// output review of a produced branch, or a grounded plan review of the repository). Rides a REVIEW step (the
/// reviewer's own verdict beat) and the reviewed producer's <see cref="JournalAgentCard"/> (the verdict chip), so the
/// adversarial exchange is legible in both places without re-parsing the reviewer's raw final message.
/// </summary>
public sealed record JournalReviewVerdict
{
    /// <summary>Whether the reviewer approved the artifact. A disapproval carries its reasons in <see cref="Issues"/>.</summary>
    public required bool Approved { get; init; }

    /// <summary>The reviewer's one-line rationale — WHY it approved / flagged.</summary>
    public required string Rationale { get; init; }

    /// <summary>The evidence-attached issues, each pre-rendered as "text (evidence: …)" — empty on an approval.</summary>
    public IReadOnlyList<string> Issues { get; init; } = Array.Empty<string>();

    /// <summary>The reviewer's own agent run — the frontend deep-links its terminal ("view reviewer run →"). NULL for a MODEL critic's verdict (an in-process call, no run to open) — the card then reads "model critic — independently prompted".</summary>
    public Guid? ReviewerRunId { get; init; }

    /// <summary>The harness the reviewer ran on (e.g. <c>claude-code</c> when the producer ran <c>codex-cli</c>) — the independence line the card shows. Null when unknown / a model critic.</summary>
    public string? ReviewerHarness { get; init; }

    /// <summary>WHAT was reviewed — <see cref="OutputScope"/> (a produced change) or <see cref="PlanScope"/> (a plan verified against the repository).</summary>
    public required string Scope { get; init; }

    /// <summary>The output-review scope value.</summary>
    public const string OutputScope = "output";

    /// <summary>The grounded plan-review scope value.</summary>
    public const string PlanScope = "plan";
}
