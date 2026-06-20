namespace CodeSpace.Messages.Decisions;

/// <summary>What the supervisor arbiter (D4c) decided to do with a pending child decision — answer it itself, or escalate it to a human.</summary>
public static class ArbiterVerdictKinds
{
    /// <summary>The arbiter answers the decision itself (only ever a low/med-risk, floor-passing decision it is confident about).</summary>
    public const string Answer = "answer";

    /// <summary>The arbiter sends the decision to a human (unsure, high-stakes, or insufficient context) — the safe default.</summary>
    public const string Escalate = "escalate";
}

/// <summary>
/// The supervisor arbiter's verdict on ONE pending decision (Decision substrate D4c, Rule 18.1 noun) — the projected,
/// already-fail-closed output of the arbiter brain. <see cref="Kind"/> is answer/escalate; an <c>answer</c> carries the
/// chosen option(s) / free text; BOTH carry a <see cref="Rationale"/> (AC3 — never silent: an auto-answer records why,
/// an escalation tells the human why it was raised). A malformed / unknown model verdict projects to <c>escalate</c>.
/// </summary>
public sealed record ArbiterVerdict
{
    public required string Kind { get; init; }

    public IReadOnlyList<string> SelectedOptions { get; init; } = Array.Empty<string>();

    public string? FreeText { get; init; }

    public required string Rationale { get; init; }

    public bool IsAnswer => Kind == ArbiterVerdictKinds.Answer;

    public static ArbiterVerdict Escalate(string rationale) => new() { Kind = ArbiterVerdictKinds.Escalate, Rationale = rationale };

    public static ArbiterVerdict Answer(IReadOnlyList<string> selectedOptions, string? freeText, string rationale) =>
        new() { Kind = ArbiterVerdictKinds.Answer, SelectedOptions = selectedOptions, FreeText = freeText, Rationale = rationale };
}
