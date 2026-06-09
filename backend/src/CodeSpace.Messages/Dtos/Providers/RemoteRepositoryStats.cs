namespace CodeSpace.Messages.Dtos.Providers;

/// <summary>
/// Headline repository stats for the Code tab's right sidebar (GitLab "Project information" style).
/// Every field is nullable + best-effort: a provider call that fails or doesn't expose a number leaves it
/// null and the UI simply omits that row — the panel never errors out the whole Code view over a count.
/// </summary>
public sealed record RemoteRepositoryStats
{
    public int? Stars { get; init; }
    public int? Forks { get; init; }
    public long? CommitCount { get; init; }
    public int? BranchCount { get; init; }
    public int? TagCount { get; init; }
    public int? ReleaseCount { get; init; }

    /// <summary>Repository storage in bytes (GitLab statistics.storage_size; GitHub repo size × 1024).</summary>
    public long? StorageBytes { get; init; }
}
