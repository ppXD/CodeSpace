using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Routing;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Defaults;

public sealed partial class StorageDefaultMaterializer
{
    /// <summary>
    /// Reads the template under a SHARE lock and refuses to proceed on anything but an enabled one.
    ///
    /// <para><c>FOR SHARE</c> rather than a plain read, and rather than <c>FOR UPDATE</c>. It has to be a lock because
    /// <c>SetEnabledAsync</c> deliberately does NOT advance <c>Revision</c> — a disable/enable cycle produces no
    /// different profile, so bumping the number would report every current team as stale — which means a materializer
    /// that re-checked by comparing revisions would see an unchanged number and activate an irreversible route from a
    /// template the operator had already switched off. It is SHARE rather than UPDATE because materializers do not
    /// modify the template: many teams may materialize at once, and only an operator EDIT has to wait.</para>
    /// </summary>
    private async Task LoadTemplateAsync(CancellationToken cancellationToken)
    {
        var template = await _db.StorageDefault
            .FromSql($"SELECT *, xmin FROM storage_default WHERE data_class_type_key = {_ctx.DataClassTypeKey} FOR SHARE")
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false) ?? throw Halt(new StorageMaterialization.NoTemplate());

        if (!template.IsEnabled) throw Halt(new StorageMaterialization.TemplateDisabled());

        _ctx.Template = template;
        _ctx.Module = _catalog.Require(template.ProviderTypeKey);
        _ctx.Subdivision = StorageDefaultRules.RequireTeamNamespace(_ctx.Module);
    }

    /// <summary>
    /// An Explicit template is never materialized by a first write. The class that declares a local home is taken off
    /// it for good by materialization — an Active route cannot go back to Draft, Retired is terminal, and a route
    /// cannot be deleted — so the choice has to be someone's, not a side effect of the first artifact that overflowed.
    /// </summary>
    private void EnsureAdoptionChosen()
    {
        if (!_ctx.Automatic || _ctx.Template.AdoptionPolicy == StorageDefaultAdoptionPolicy.Automatic) return;

        throw Halt(new StorageMaterialization.AdoptionRequiresChoice());
    }

    /// <summary>
    /// Two distinct claims, in the order that makes the second meaningful.
    ///
    /// <para>An existing provenance row means THIS pipeline already ran for this (team, class) — idempotent success,
    /// and the caller may treat it as such. An existing route without one means the team configured its own
    /// destination, or the shipped agent-run-log bootstrap did; either way a default must not displace it. The team's
    /// own configuration always wins, which is what makes it a default rather than a policy.</para>
    /// </summary>
    private async Task EnsureNothingClaimsThisClassAsync(CancellationToken cancellationToken)
    {
        var materialization = await _db.StorageDefaultMaterialization.AsNoTracking()
            .SingleOrDefaultAsync(row => row.TeamId == _ctx.TeamId && row.DataClassTypeKey == _ctx.DataClassTypeKey, cancellationToken)
            .ConfigureAwait(false);

        if (materialization != null)
            throw Halt(new StorageMaterialization.AlreadyMaterialized(materialization.StorageProfileId, materialization.SourceRevision));

        var route = await _db.StorageRoute.AsNoTracking()
            .SingleOrDefaultAsync(row => row.TeamId == _ctx.TeamId && row.DataClassTypeKey == _ctx.DataClassTypeKey, cancellationToken)
            .ConfigureAwait(false);

        if (route != null) throw Halt(new StorageMaterialization.TeamOwnsRoute(route.Id));

        if (!await _db.Team.AsNoTracking().AnyAsync(team => team.Id == _ctx.TeamId, cancellationToken).ConfigureAwait(false))
            throw Halt(new StorageMaterialization.TeamNotFound());
    }
}
