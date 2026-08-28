using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Artifacts.Routing;
using CodeSpace.Messages.Dtos.Storage;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Defaults;

/// <summary>
/// Three reads and a join: the classes this build knows, the templates the deployment authored, and what this team
/// already has.
///
/// <para>EVERY class is reported, including those with no default at all. A screen that listed only the ones with a
/// default could not explain the absence — and "the deployment has authored nothing" is the state every new
/// deployment is in, so it is the answer most often needed.</para>
/// </summary>
public sealed class StorageAdoptionReader : IStorageAdoptionReader, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IRoutedDataClassCatalog _catalog;

    public StorageAdoptionReader(CodeSpaceDbContext db, IRoutedDataClassCatalog catalog)
    {
        _db = db;
        _catalog = catalog;
    }

    public async Task<IReadOnlyList<StorageAdoptionStatus>> ReadAsync(Guid teamId, CancellationToken cancellationToken)
    {
        var templates = await _db.StorageDefault.AsNoTracking()
            .Select(row => new Template(row.DataClassTypeKey, row.Revision, row.IsEnabled))
            .ToDictionaryAsync(row => row.DataClassTypeKey, cancellationToken).ConfigureAwait(false);

        var adoptions = await _db.StorageDefaultMaterialization.AsNoTracking()
            .Where(row => row.TeamId == teamId)
            .Select(row => new Adoption(row.DataClassTypeKey, row.SourceRevision))
            .ToDictionaryAsync(row => row.DataClassTypeKey, cancellationToken).ConfigureAwait(false);

        var routed = await _db.StorageRoute.AsNoTracking()
            .Where(row => row.TeamId == teamId)
            .Select(row => row.DataClassTypeKey)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var routedClasses = routed.ToHashSet(StringComparer.Ordinal);

        return _catalog.DataClasses.Select(dataClass => Describe(dataClass, templates, adoptions, routedClasses)).ToList();
    }

    /// <summary>
    /// One class's answer. <c>CanAdopt</c> is computed HERE rather than left to the caller so the rule lives in one
    /// place: a screen that re-derived it would drift from the materializer, and the two disagreeing means offering a
    /// button that refuses — or hiding one that would have worked.
    /// </summary>
    private static StorageAdoptionStatus Describe(IRoutedDataClass dataClass, IReadOnlyDictionary<string, Template> templates,
        IReadOnlyDictionary<string, Adoption> adoptions, IReadOnlySet<string> routedClasses)
    {
        var template = templates.GetValueOrDefault(dataClass.TypeKey);
        var adoption = adoptions.GetValueOrDefault(dataClass.TypeKey);
        var available = template is { IsEnabled: true };

        // A materialized team HAS a route — the materializer made it. "The team owns the route" therefore means a
        // route the deployment did not create, which is the only kind a default must never displace.
        var teamOwnsRoute = adoption == null && routedClasses.Contains(dataClass.TypeKey);

        return new StorageAdoptionStatus
        {
            DataClassTypeKey = dataClass.TypeKey,
            DisplayName = dataClass.DisplayName,
            DefaultAvailable = available,
            Adopted = adoption != null,
            TeamOwnsRoute = teamOwnsRoute,
            CanAdopt = available && adoption == null && !teamOwnsRoute,

            // The same predicate StorageDefaultRules.EnsureAdoptionPolicyAllowed refuses an Automatic policy on, and
            // for the same reason: a class that declares a local home loses it for good once its route is Active.
            AdoptionIsIrreversible = dataClass is IRoutedDataClassLocalFallback,

            SourceRevision = adoption?.SourceRevision,
            TemplateRevision = template?.Revision,
        };
    }

    private sealed record Template(string DataClassTypeKey, int Revision, bool IsEnabled);
    private sealed record Adoption(string DataClassTypeKey, int SourceRevision);
}
