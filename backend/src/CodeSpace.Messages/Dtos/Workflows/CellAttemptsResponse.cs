using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Workflows;

/// <summary>
/// The per-CELL attempt history — every attempt in a lineage that ran one <c>(nodeId, iterationKey)</c> cell (a node,
/// or one map branch), oldest first. Lets the run detail show, on a re-run node/branch, each attempt's own record so
/// you can look back at the earlier (e.g. failed) run, not only the latest the merged view surfaces.
/// </summary>
public sealed record CellAttemptsResponse
{
    /// <summary>Oldest → newest. A cell only one attempt ran yields a single entry.</summary>
    public required IReadOnlyList<CellAttempt> Attempts { get; init; }
}

/// <summary>One attempt's run of a cell — which attempt, its agent run, and how that attempt's cell ended.</summary>
public sealed record CellAttempt
{
    /// <summary>1-based ordinal of the owning RUN within the lineage (1 = the original).</summary>
    public required int AttemptNumber { get; init; }

    public required Guid RunId { get; init; }

    /// <summary>The agent run this attempt spawned for the cell (null if the cell wasn't an agent node on that attempt).</summary>
    public string? AgentRunId { get; init; }

    /// <summary>This attempt's cell outcome (the node status for that run).</summary>
    public required NodeStatus Status { get; init; }

    public required DateTimeOffset CreatedDate { get; init; }

    /// <summary>True for the newest attempt that ran the cell — the one the merged detail shows by default.</summary>
    public required bool IsLatest { get; init; }
}
