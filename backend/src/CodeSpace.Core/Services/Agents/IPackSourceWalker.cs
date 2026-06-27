using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Recursively discovers agents + skills in a local pack source tree (a cloned repo, a checked-out workspace).
/// The fetch-agnostic core of pack import: whatever put the files on disk (a git clone of a pasted URL, a
/// registered-repo checkout), this walks the tree, classifies each Markdown artifact, parses it via the agent /
/// skill parser registries, and returns a <see cref="DiscoveredPack"/>. Stateless + safe to share.
/// </summary>
public interface IPackSourceWalker
{
    /// <summary>
    /// Walk <paramref name="rootDir"/> recursively, returning every discovered agent + skill with its root-relative
    /// source path. Throws ONLY when the directory does not exist. A single malformed or unreadable file never aborts
    /// the walk: a <c>SKILL.md</c> always appears (a malformed/unreadable one rides along with diagnostics), while a
    /// <c>.md</c> is classified as an agent only if it parses to a name — so a README / doc / unreadable <c>.md</c> is
    /// simply not an agent (correctly excluded, not a ridden-along error).
    /// </summary>
    Task<DiscoveredPack> WalkAsync(string rootDir, CancellationToken cancellationToken);
}
