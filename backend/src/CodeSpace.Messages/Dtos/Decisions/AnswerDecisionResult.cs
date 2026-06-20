namespace CodeSpace.Messages.Dtos.Decisions;

/// <summary>Outcome of answering a pending decision through the cross-grain queue (Decision substrate D3b).</summary>
public enum DecisionAnswerOutcome
{
    /// <summary>This call resolved the decision (the agent's mid-run call unblocks / the workflow resumes).</summary>
    Answered,

    /// <summary>The decision was already resolved (a card click, another answerer, or the deadline won first) — an idempotent no-op, never a double-resolve.</summary>
    AlreadyResolved,

    /// <summary>No pending decision with that id is visible to the caller's team (absent, already terminal, or another team's — never distinguished, to avoid a cross-team existence leak).</summary>
    NotFound,

    /// <summary>The answer doesn't fit the decision — an option id that isn't one of the choices, or an empty answer to a free-text ask.</summary>
    Invalid,
}

/// <summary>The result of an <c>AnswerDecisionCommand</c> — the outcome plus an optional human-readable reason. A Rule 18.1 pure data noun.</summary>
public sealed record AnswerDecisionResult
{
    public required DecisionAnswerOutcome Outcome { get; init; }

    public string? Message { get; init; }

    public static AnswerDecisionResult Of(DecisionAnswerOutcome outcome, string? message = null) => new() { Outcome = outcome, Message = message };
}
