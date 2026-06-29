namespace CodeSpace.Core.Services.Sessions.Room;

/// <summary>
/// The per-turn facts the projector gathers from the substrate (the decision tape, the agents' results, the PR node)
/// and hands to the PURE <see cref="RoomNarrative"/>. Keeping the I/O here and the rendering pure means the narrative
/// engine stays unit-testable and the gathering stays one bounded, focused-turn read set (no N+1). All members default
/// empty, so a turn with nothing to say projects exactly as before — <see cref="Empty"/> is the inert baseline.
/// </summary>
public sealed record RoomTurnFacts
{
    /// <summary>The plan's subtask titles (bounded ≤ 20 by schema) — the "Planned N subtasks" row.</summary>
    public IReadOnlyList<string> Subtasks { get; init; } = Array.Empty<string>();

    /// <summary>The distinct changed-file paths across the turn's agents — the "Changed N files" row.</summary>
    public IReadOnlyList<string> ChangedFiles { get; init; } = Array.Empty<string>();

    /// <summary>+added / −removed line totals across the turn, when captured (a true data-gap today → null → the row omits "+X −Y").</summary>
    public int? Additions { get; init; }
    public int? Deletions { get; init; }

    /// <summary>Total side-effecting tool calls across the turn's agents — the "N tool calls" row. Null when unknown.</summary>
    public int? ToolCalls { get; init; }

    /// <summary>The per-kind tool-call histogram across the turn's agents (e.g. read · edit · test) — the Tools row's detail + items. Empty when unknown.</summary>
    public IReadOnlyList<ToolKindCount> ToolHistogram { get; init; } = Array.Empty<ToolKindCount>();

    /// <summary>Each spawned agent's one-line result summary, keyed by its run id — the agent cards' takeaway and the lead fallback when there's no stop summary. Empty when none produced one.</summary>
    public IReadOnlyDictionary<Guid, string> AgentSummaries { get; init; } = new Dictionary<Guid, string>();

    /// <summary>How many reasoning entries the turn produced — the "Reasoning" row's count.</summary>
    public int ReasoningCount { get; init; }

    /// <summary>The turn's reasoning step texts (bounded), surfaced when the Reasoning row is expanded. Empty when none produced.</summary>
    public IReadOnlyList<string> ReasoningSteps { get; init; } = Array.Empty<string>();

    /// <summary>The objective acceptance verdict (the Review stage): true = passed, false = failed, null = not graded.</summary>
    public bool? AcceptancePassed { get; init; }

    /// <summary>The delivered change set (the PR card), when the turn opened one.</summary>
    public RoomDelivery? Delivery { get; init; }

    /// <summary>The raw engine error, surfaced behind "Show raw error" on a failure diagnostic. Null when there's nothing rawer than the humanized text.</summary>
    public string? RawError { get; init; }

    public static readonly RoomTurnFacts Empty = new();
}

/// <summary>One bucket of the tool-call histogram — a tool kind and how many times the turn's agents called it.</summary>
public sealed record ToolKindCount(string Kind, int Count);

/// <summary>The PR / change set a turn produced — joined from the run's open-PR node. Provider-agnostic.</summary>
public sealed record RoomDelivery
{
    public required string Title { get; init; }
    public string? Reference { get; init; }
    public string? BranchHead { get; init; }
    public string? BranchBase { get; init; }
    public string? Checks { get; init; }
    public bool? ChecksOk { get; init; }
    public string? Url { get; init; }
}
