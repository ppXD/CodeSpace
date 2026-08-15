using CodeSpace.Core.Persistence.Db;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>Reads only the three public identity fields needed by qualification; configuration history stays cold.</summary>
public sealed class StorageProfileProbeTargetResolver : IStorageProfileProbeTargetResolver
{
    private readonly CodeSpaceDbContext _db;

    public StorageProfileProbeTargetResolver(CodeSpaceDbContext db) { _db = db; }

    public Task<StorageProfileProbeTarget?> ResolveAsync(StorageProfileProbeTargetRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return (
            from profile in _db.StorageProfile.AsNoTracking()
            let selectedRevision = request.ProfileRevision ?? profile.CurrentRevision
            join revision in _db.StorageProfileRevision.AsNoTracking()
                on new { profile.TeamId, StorageProfileId = profile.Id, Revision = selectedRevision }
                equals new { revision.TeamId, revision.StorageProfileId, revision.Revision } into exactRevisions
            from revision in exactRevisions.DefaultIfEmpty()
            where profile.TeamId == request.TeamId && profile.Id == request.ProfileId
            select new StorageProfileProbeTarget(profile.Id, selectedRevision, revision == null ? null : revision.ProviderTypeKey))
            .SingleOrDefaultAsync(cancellationToken);
    }
}
