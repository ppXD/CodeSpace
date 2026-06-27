namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Fetches a pack from a remote URL into a transient local checkout the <see cref="IPackSourceWalker"/> can walk.
/// The "paste a URL → load all agents/skills" entry point: it enforces the host allowlist (egress guard) and
/// clones to a temp dir. The returned <see cref="PackCheckout"/> MUST be disposed (a <c>using</c>) once discovery
/// + import are done, so the clone is removed and transient clones can't fill the worker's disk.
/// </summary>
public interface IPackSourceFetcher
{
    /// <summary>Clone <paramref name="url"/> (at the optional branch/tag <paramref name="reference"/>; null = the default branch) into a transient dir. Throws <see cref="PackImportException"/> when the host is not allowlisted or the clone fails.</summary>
    Task<PackCheckout> FetchAsync(string url, string? reference, CancellationToken cancellationToken);
}
