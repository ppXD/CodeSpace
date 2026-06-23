namespace CodeSpace.Messages.Agents;

/// <summary>
/// The scoped, source-agnostic retrieval request a <c>get_context</c> call hands to every context source. It carries
/// only IDENTITY + an optional refinement — never which source to read (the registry dispatches on that) — so a NEW
/// source plugs in without widening this shape. Identity is the run's OWN team + run (stamped from the per-run MCP
/// endpoint, same provenance as the tool's other trusted fields); <see cref="SessionId"/> is the run's resolved work
/// thread (null when the run is session-less → the session sources fail-soft to "found nothing"). Every source MUST
/// re-key its own reads on <see cref="TeamId"/> (tenancy is never assumed from the run alone).
/// </summary>
public sealed record AgentContextQuery
{
    /// <summary>The run's owning team — every source re-filters its reads on this (fail-closed tenancy).</summary>
    public required Guid TeamId { get; init; }

    /// <summary>The agent run the call serves — for a source that reads run-relative context (e.g. sibling units). The session sources read <see cref="SessionId"/> instead.</summary>
    public required Guid RunId { get; init; }

    /// <summary>The work thread this run is a turn of, pre-resolved by the tool (<c>AgentRun → WorkflowRun.SessionId</c>). Null = the run is not part of a thread → the session sources return nothing.</summary>
    public Guid? SessionId { get; init; }

    /// <summary>Optional free-text refinement the source interprets its own way (the session-turns source treats it as a case-insensitive filter; a future semantic source as the search query). Null/blank = no refinement.</summary>
    public string? Query { get; init; }
}

/// <summary>
/// One source's answer to an <see cref="AgentContextQuery"/>: the retrieved <see cref="Text"/> and whether the source
/// actually had anything (<see cref="Found"/> = false is a clean "nothing here", NOT an error — a source with no
/// matching context returns <see cref="Empty"/>, never throws). The tool composes one or many of these into the wire
/// result the model receives.
/// </summary>
public sealed record AgentContextResult
{
    /// <summary>True when the source produced content; false = a clean miss (no session, no matching turns, no summary yet).</summary>
    public required bool Found { get; init; }

    /// <summary>The retrieved content (already bounded by the source). Empty string when <see cref="Found"/> is false.</summary>
    public string Text { get; init; } = "";

    /// <summary>A clean miss — the source had nothing to return.</summary>
    public static AgentContextResult Empty { get; } = new() { Found = false };

    /// <summary>A hit carrying <paramref name="text"/>.</summary>
    public static AgentContextResult From(string text) => new() { Found = true, Text = text };
}
