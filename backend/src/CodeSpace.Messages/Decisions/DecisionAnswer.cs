namespace CodeSpace.Messages.Decisions;

/// <summary>
/// The resolution of a <see cref="DecisionRequest"/> (Rule 18.1 noun) — what gets written onto the durable wait /
/// ledger row on resolve and injected back as the raiser's resume payload at its EXACT waiting point. The same
/// shape regardless of who answered (a human from the queue, a policy auto-answer, a supervisor arbiter, or the
/// bounded-wait deadline applying the default), so the raiser maps it to outputs identically.
///
/// <para>Every NON-human answer MUST carry a <see cref="Rationale"/> (AC3 — an auto-answer is never silent), so the
/// Run Activity trace can always explain why a decision was made on a person's behalf.</para>
/// </summary>
public sealed record DecisionAnswer
{
    /// <summary>The decision this answers.</summary>
    public required Guid DecisionId { get; init; }

    /// <summary>Who answered — see <see cref="DecisionAnsweredByKinds"/>.</summary>
    public required string AnsweredBy { get; init; }

    /// <summary>The chosen option id(s) — one for confirm/choose_one/approve_action, many for choose_many. Empty for a pure free-text answer.</summary>
    public IReadOnlyList<string> SelectedOptions { get; init; } = Array.Empty<string>();

    /// <summary>The free-text answer (for free_text), or an optional comment alongside a choice.</summary>
    public string? FreeText { get; init; }

    /// <summary>Why this answer was given — REQUIRED for any non-human answer (policy / supervisor / timeout); the audit + trace source.</summary>
    public string? Rationale { get; init; }

    /// <summary>The user id of a human answerer (null for policy / supervisor / timeout).</summary>
    public Guid? AnsweredByUserId { get; init; }

    /// <summary>True when the bounded-wait deadline applied the default rather than anyone answering.</summary>
    public bool TimedOut { get; init; }
}
