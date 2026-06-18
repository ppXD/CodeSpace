namespace CodeSpace.Messages.Agents;

/// <summary>
/// ONE repository that could not be auto-combined in a MULTI-repo merge (resolver loop #379, S7-D2) — the durable
/// input the per-repo resolver recipe is assembled from. A pure data noun (Rule 18.1): which repo (id + alias /
/// workspace subdirectory) conflicted, and the files that conflicted. The branches the resolver reconciles for this
/// repo come from the spawn's agent results (the FULL per-repo set), not this block (which names only the conflicting
/// subset), mirroring the single-repo recipe.
/// </summary>
public sealed record SupervisorConflictedRepo
{
    /// <summary>The conflicted repository's id — the per-repo branch-collection key. Null only on a degraded block with no resolvable repository id.</summary>
    public Guid? RepositoryId { get; init; }

    /// <summary>The repo's alias within the workspace (its subdirectory the resolver reconciles in).</summary>
    public required string Alias { get; init; }

    /// <summary>The files that conflicted in this repo — surfaced to the resolver as the spots to pay special attention to.</summary>
    public IReadOnlyList<string> ConflictedFiles { get; init; } = Array.Empty<string>();
}
