using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Publish.Exceptions;

namespace CodeSpace.Core.Services.Agents.Publish;

/// <summary>
/// Pure repository authority rule shared by manifest-backed execution consumers. A concrete repository id is
/// authoritative only on an exact unique match. The sole null-id row remains the legacy single-repository carrier;
/// a concrete mismatch never inherits that compatibility fallback.
/// </summary>
public static class PublishManifestRepositorySelector
{
    public static PublishManifest? Select(IReadOnlyList<PublishManifest> manifests, Guid repositoryId)
    {
        PublishManifest? exact = null;
        var exactCount = 0;

        foreach (var manifest in manifests)
        {
            if (manifest.RepositoryId != repositoryId) continue;

            exact ??= manifest;
            exactCount++;
        }

        if (exactCount > 1) throw new PublishManifestRepositorySelectionException(repositoryId, exactCount);
        if (exact is not null) return exact;

        return manifests.Count == 1 && manifests[0].RepositoryId is null ? manifests[0] : null;
    }
}
