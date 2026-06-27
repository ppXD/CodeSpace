using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// URL-driven, multi-format pack import: clone a pack from a git URL and discover its agents + skills. The
/// successor to the repository-bound, agent-only <c>IAgentPackImportService</c> — it composes the fetcher
/// (host-allowlist + clone) + the walker (recursive discovery) + per-team conflict checks. Preview persists
/// nothing (the transient clone is reclaimed); the commit path lands in a later slice.
/// </summary>
public interface IPackImportService
{
    /// <summary>Clone <paramref name="url"/> (at the optional branch/tag <paramref name="reference"/>) and return a dry-run preview of every discovered agent + skill with its derived handle, slug-conflict flag, and importability for <paramref name="teamId"/>.</summary>
    Task<PackPreview> PreviewFromUrlAsync(string url, string? reference, Guid teamId, CancellationToken cancellationToken);

    /// <summary>Re-clone <paramref name="url"/>, re-walk it, resolve (or create) its <c>Pack</c>, and persist exactly the chosen <paramref name="sourcePaths"/> — agents and skills — for <paramref name="teamId"/>. Idempotent: a re-run upserts on (pack, source-path) rather than duplicating. Returns a per-path outcome.</summary>
    Task<PackImportResult> ImportFromUrlAsync(string url, string? reference, IReadOnlyList<string> sourcePaths, Guid teamId, Guid actorUserId, CancellationToken cancellationToken);

    /// <summary>Re-pull the pack <paramref name="packId"/> from its saved source: refresh every already-imported artifact in place (kept handles) and return what changed plus the discovered-but-not-imported artifacts as a preview to add.</summary>
    Task<PackSyncResult> SyncAsync(Guid teamId, Guid packId, Guid actorUserId, CancellationToken cancellationToken);
}
