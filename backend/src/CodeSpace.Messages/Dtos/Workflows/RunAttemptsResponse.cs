using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Workflows;

/// <summary>
/// The attempt ladder of one lineage — the original run plus every replay/rerun fork of it, oldest first. Drives the
/// run-detail attempt switcher: open any run in the lineage and you get the whole ladder, with the latest flagged.
/// </summary>
public sealed record RunAttemptsResponse
{
    /// <summary>The lineage root (the original run) — the stable identity the list + detail title on.</summary>
    public required Guid RootRunId { get; init; }

    /// <summary>Oldest → newest. A never-rerun run yields a single attempt (itself).</summary>
    public required IReadOnlyList<RunAttemptSummary> Attempts { get; init; }
}

/// <summary>One attempt in a lineage — its run id, 1-based ordinal, and how it ended.</summary>
public sealed record RunAttemptSummary
{
    public required Guid RunId { get; init; }

    /// <summary>1-based ordinal within the lineage (1 = the original).</summary>
    public required int AttemptNumber { get; init; }

    public required WorkflowRunStatus Status { get; init; }

    /// <summary>This attempt's own source — "replay"/"rerun" for a fork, the original's source for attempt 1.</summary>
    public required string SourceType { get; init; }

    /// <summary>The node this attempt re-ran from (the map node for a branch rerun); null for the original or a whole-run replay. Drives the per-node rerun history.</summary>
    public string? RerunFromNodeId { get; init; }

    public required DateTimeOffset CreatedDate { get; init; }

    /// <summary>True for the newest attempt — the one the detail selects by default.</summary>
    public required bool IsLatest { get; init; }
}
