namespace CodeSpace.Messages.Dtos.Sessions.Journal;

/// <summary>
/// The structured facts of ONE model call — enriched onto a <c>model_call</c> journal step so the model-call fold, once
/// expanded, shows a legible row (purpose · model · tokens · latency · cost · status) instead of a bare "Model call"
/// line. A model call is the substance + cost of an AI workflow, so it is SEEN (default-folded), not downgraded. Read off
/// the durable <c>interaction.completed</c> / <c>interaction.failed</c> ledger record (paired with its
/// <c>interaction.started</c> for latency) by <c>ModelCallFactsSource</c>. Cost is fail-open null on an unpriced /
/// unknown model; latency null when the start wasn't paired; tokens null when the call reported no usage.
/// </summary>
public sealed record JournalModelCall
{
    /// <summary>What the call was FOR — the interaction kind the caller stamped (e.g. <c>supervisor.decision</c>, <c>plan.author</c>, <c>llm.complete</c>). The frontend maps the common kinds to a friendly word (decision / planner / …), else shows it verbatim.</summary>
    public required string Purpose { get; init; }

    /// <summary>The model the call ran on (e.g. <c>claude-opus-4-8</c>). Null when the record didn't name one.</summary>
    public string? Model { get; init; }

    /// <summary>Prompt (input) tokens the call consumed. Null when the call reported no usage.</summary>
    public int? InputTokens { get; init; }

    /// <summary>Completion (output) tokens the call produced. Null when the call reported no usage.</summary>
    public int? OutputTokens { get; init; }

    /// <summary>Total tokens (input + output) — the fold's summable cost signal. Null when the call reported no usage (0 would read as "measured zero").</summary>
    public int? Tokens { get; init; }

    /// <summary>Wall-clock latency in milliseconds — the start→completion span of the paired ledger records. Null when the start couldn't be paired.</summary>
    public long? LatencyMs { get; init; }

    /// <summary>Realized USD cost (model × tokens via the shared pricing), fail-open null on an unpriced / unknown model — the SAME pricing the agent cards use, so a per-call cost can't disagree with a run total.</summary>
    public decimal? CostUsd { get; init; }

    /// <summary>The call's outcome — <c>completed</c> or <c>failed</c>.</summary>
    public required string Status { get; init; }
}
