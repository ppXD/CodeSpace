using CodeSpace.Messages.Dtos.Agents;

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
}
