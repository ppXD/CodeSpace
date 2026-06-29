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

    /// <summary>How many reasoning entries the turn produced — the "Reasoning" row's count. The text is fetched lazily on expand (never loaded into the projection), so a huge run stays cheap.</summary>
    public int ReasoningCount { get; init; }

    /// <summary>The objective acceptance verdict (the Review stage): true = passed, false = failed, null = not graded.</summary>
    public bool? AcceptancePassed { get; init; }

    /// <summary>The delivered change set (the PR card), when the turn opened one.</summary>
    public RoomDelivery? Delivery { get; init; }

    /// <summary>The raw engine error, surfaced behind "Show raw error" on a failure diagnostic. Null when there's nothing rawer than the humanized text.</summary>
    public string? RawError { get; init; }

    public static readonly RoomTurnFacts Empty = new();
}

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
