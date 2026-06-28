using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// The Library/store READ model over imported <c>Pack</c>s — the source categories and their contents. Pure
/// reads; the import/sync mechanics live on <see cref="IPackImportService"/>.
/// </summary>
public interface IPackService
{
    /// <summary>The team's active packs (the store's source rail) with freshness + active agent/skill counts, ordered by name.</summary>
    Task<IReadOnlyList<PackSummary>> ListAsync(Guid teamId, CancellationToken cancellationToken);

    /// <summary>One pack with its active agents + skills (the store detail pane), or null when it doesn't exist in <paramref name="teamId"/>.</summary>
    Task<PackDetail?> GetAsync(Guid teamId, Guid packId, CancellationToken cancellationToken);

    /// <summary>One server-side page of a pack's active STORE artifacts of a single <paramref name="kind"/>, optionally filtered by name/handle. Backs the paginated Library detail tab and the pickers. <paramref name="page"/> is 0-based and clamped to the available range. A pack absent from <paramref name="teamId"/> yields an empty page.</summary>
    Task<PagedArtifacts> ListArtifactsAsync(Guid teamId, Guid packId, PackArtifactKind kind, string? search, int page, int pageSize, CancellationToken cancellationToken);
}
