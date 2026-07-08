namespace CodeSpace.Messages.Agents;

/// <summary>
/// The outcome of resolving a dependent subtask's spawn-time workspace staging (S1) off its producers' recorded
/// <see cref="Persistence.Entities.PublishManifest"/> rows — never the repository's default branch as a silent
/// fallback. A pure data noun (Rule 18.1) returned by the staging resolver; consumed by
/// <c>RealSupervisorActionExecutor.BuildAgentTask</c> to override the spawned agent's clone ref and prepend a
/// server-authored handoff block to its goal.
/// </summary>
public sealed record DependencyStagingResult
{
    /// <summary>No dependency declared, or every dependency made no changes to this repository — the byte-identical default-branch path (no override).</summary>
    public static readonly DependencyStagingResult NoOverride = new();

    /// <summary>The ref the dependent agent should clone at — a single producer's own branch, or a fresh run integration branch combining several. Null when <see cref="NoOverride"/> or <see cref="BlockedReason"/> is set.</summary>
    public string? Ref { get; init; }

    /// <summary>The server-authored block (producer branch(es) + summary + file count) to prepend to the dependent's goal, so the agent's prompt names what it is building on. Null when <see cref="Ref"/> is null.</summary>
    public string? GoalFoldText { get; init; }

    /// <summary>Non-null ⇒ the subtask must NOT be spawned this turn — a producer's manifest carries neither a branch nor a patch (an I1 violation), or its work could not be auto-integrated. Never silently defaults to the repository's default branch.</summary>
    public string? BlockedReason { get; init; }

    /// <summary>The repo-relative paths that conflicted while integrating the producers' work (empty unless the block was a conflict, not a missing-manifest fail-closed).</summary>
    public IReadOnlyList<string> ConflictedFiles { get; init; } = Array.Empty<string>();

    /// <summary>The producers' own branches preserved for review/reconciliation when integration conflicted (empty unless the block was a conflict).</summary>
    public IReadOnlyList<string> PreservedBranches { get; init; } = Array.Empty<string>();

    /// <summary>Whether this subtask's spawn must be withheld this turn.</summary>
    public bool IsBlocked => BlockedReason is not null;
}
