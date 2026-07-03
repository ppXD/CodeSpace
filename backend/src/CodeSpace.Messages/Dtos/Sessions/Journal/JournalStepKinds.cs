namespace CodeSpace.Messages.Dtos.Sessions.Journal;

/// <summary>
/// The well-known <see cref="JournalStep.Kind"/> values — the frontend's render discriminator for a journal step. OPEN
/// on purpose (a describer may emit a new kind and the frontend degrades an unknown one to a generic step, never drops
/// it), so this is a vocabulary, not a closed enum. Pinned as constants so a describer + a test name the same string.
/// </summary>
public static class JournalStepKinds
{
    /// <summary>A supervisor decision (plan / spawn / retry / merge / resolve / ask_human / stop) — the orchestration story beat.</summary>
    public const string Decision = "decision";

    /// <summary>A side-effecting tool call (a git.open_pr, a governed command) — what the agent did to the world.</summary>
    public const string Tool = "tool";

    /// <summary>An agent's own narrative event (a file edit, a test result, an error, its final summary).</summary>
    public const string Agent = "agent";

    /// <summary>An agent's REASONING beat — a folded "thinking" summary inserted chronologically (the chain-of-thought), distinct from its narrative <see cref="Agent"/> events. The frontend renders it italic + collapsed by default.</summary>
    public const string Thinking = "thinking";

    /// <summary>A run / node lifecycle beat (started / completed / failed).</summary>
    public const string Lifecycle = "lifecycle";

    /// <summary>A model call's outcome (the supervisor brain / a node LLM call) — the "deciding…" beat with its token cost.</summary>
    public const string ModelCall = "model_call";

    /// <summary>The generic FALLBACK kind — an event no specific describer claimed still renders as a plain step under this kind (never dropped).</summary>
    public const string Event = "event";
}
