using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Routing.Exceptions;
using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Dtos.Storage;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Routing;

/// <summary>
/// Team-admin control plane for storage routing policy. This service never resolves a runtime driver and never writes,
/// reads, moves, or verifies an artifact — but the rows it writes ARE what the runtime reads, so a state change here
/// changes where the next offloaded write lands.
///
/// <para>What creating a route does today: a new route is born Draft, and Draft is inert for the
/// <c>workflow-artifact/v1</c> class — those writes keep the local blob backend until an operator activates the route
/// (see <c>WorkflowArtifactDestinationResolver</c>). It is NOT inert for <c>agent-run-log/v1</c>: that class has no
/// local backend, so its resolver reports an un-activated route as unavailable capture
/// (see <c>AgentRunLogStorageResolver</c>). Activating, disabling or retiring a route takes effect on the next write
/// either way; bytes already stored keep the exact profile revision their location was stamped with.</para>
/// </summary>
public sealed class StorageRouteService : IStorageRouteService, IScopedDependency
{
    internal const string ConcurrentRouteSqlState = "P7501";
    private readonly CodeSpaceDbContext _db;
    private readonly IRoutedDataClassCatalog _dataClasses;

    public StorageRouteService(CodeSpaceDbContext db, IRoutedDataClassCatalog dataClasses)
    {
        _db = db;
        _dataClasses = dataClasses;
    }

    public async Task<StoragePage<StorageRouteSummary>> ListPageAsync(Guid teamId, string? cursor, int limit, CancellationToken cancellationToken)
    {
        var keyset = StorageSettingsCursor.Decode(cursor);
        var take = Math.Clamp(limit, 1, StoragePageLimits.MaxPageSize);
        var query =
            from route in _db.StorageRoute.AsNoTracking()
            join revision in _db.StorageRouteRevision.AsNoTracking()
                on new { route.TeamId, StorageRouteId = route.Id, Revision = route.CurrentRevision }
                equals new { revision.TeamId, revision.StorageRouteId, revision.Revision }
            join profile in _db.StorageProfile.AsNoTracking()
                on new { revision.TeamId, revision.StorageProfileId }
                equals new { profile.TeamId, StorageProfileId = profile.Id }
            where route.TeamId == teamId
            select new { Route = route, Revision = revision, ProfileStableName = profile.StableName };
        if (keyset is { } after)
            query = query.Where(row => string.Compare(row.Route.DataClassTypeKey, after.StableName) > 0
                || row.Route.DataClassTypeKey == after.StableName && row.Route.Id.CompareTo(after.Id) > 0);

        var rows = await query.OrderBy(row => row.Route.DataClassTypeKey).ThenBy(row => row.Route.Id).Take(take + 1)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var hasMore = rows.Count > take;
        var page = hasMore ? rows.GetRange(0, take) : rows;
        return new StoragePage<StorageRouteSummary>
        {
            Items = page.Select(row => Summary(row.Route, row.Revision, row.ProfileStableName)).ToList(),
            NextCursor = hasMore ? new StorageSettingsCursor(page[^1].Route.DataClassTypeKey, page[^1].Route.Id).Encode() : null,
        };
    }

    public async Task<StorageRouteDetail?> GetAsync(Guid teamId, Guid routeId, string? revisionCursor, int revisionLimit, CancellationToken cancellationToken)
    {
        var head = await (
            from route in _db.StorageRoute.AsNoTracking()
            join revision in _db.StorageRouteRevision.AsNoTracking()
                on new { route.TeamId, StorageRouteId = route.Id, Revision = route.CurrentRevision }
                equals new { revision.TeamId, revision.StorageRouteId, revision.Revision }
            join profile in _db.StorageProfile.AsNoTracking()
                on new { revision.TeamId, revision.StorageProfileId }
                equals new { profile.TeamId, StorageProfileId = profile.Id }
            where route.TeamId == teamId && route.Id == routeId
            select new { Route = route, Current = revision, ProfileStableName = profile.StableName })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (head == null) return null;

        var cursor = StorageRouteRevisionCursor.Decode(revisionCursor, routeId);
        var take = Math.Clamp(revisionLimit, 1, StorageRouteRevisionPageLimits.MaxPageSize);
        var query =
            from revision in _db.StorageRouteRevision.AsNoTracking()
            join profile in _db.StorageProfile.AsNoTracking()
                on new { revision.TeamId, revision.StorageProfileId }
                equals new { profile.TeamId, StorageProfileId = profile.Id }
            where revision.TeamId == teamId && revision.StorageRouteId == routeId
            select new { Revision = revision, ProfileStableName = profile.StableName };
        if (cursor is { } after)
            query = query.Where(row => row.Revision.Revision < after.Revision
                || row.Revision.Revision == after.Revision && row.Revision.Id.CompareTo(after.Id) < 0);

        var rows = await query.OrderByDescending(row => row.Revision.Revision).ThenByDescending(row => row.Revision.Id)
            .Take(take + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var hasMore = rows.Count > take;
        var pageRows = hasMore ? rows.GetRange(0, take) : rows;
        var page = new StoragePage<StorageRouteRevisionDetail>
        {
            Items = pageRows.Select(row => RevisionDetail(row.Revision, row.ProfileStableName)).ToList(),
            NextCursor = hasMore ? new StorageRouteRevisionCursor(routeId, pageRows[^1].Revision.Revision, pageRows[^1].Revision.Id).Encode() : null,
        };
        return Detail(head.Route, RevisionDetail(head.Current, head.ProfileStableName), page);
    }

    public async Task<StorageRouteDetail> CreateAsync(Guid teamId, Guid actorId, CreateStorageRouteCommand command, CancellationToken cancellationToken)
    {
        var dataClassTypeKey = ExecuteRule(() => StorageRouteRules.NormalizeDataClassTypeKey(command.DataClassTypeKey));
        EnsureRoutedDataClass(dataClassTypeKey);
        var selection = Selection(command.ProfileRevisionMode, command.PinnedProfileRevision);
        var profile = await RequireActiveProfileAsync(teamId, command.StorageProfileId, selection, cancellationToken).ConfigureAwait(false);
        if (await _db.StorageRoute.AsNoTracking().AnyAsync(route => route.TeamId == teamId && route.DataClassTypeKey == dataClassTypeKey, cancellationToken).ConfigureAwait(false))
            throw new StorageRouteConflictException($"Storage route '{dataClassTypeKey}' already exists in this team.");

        var now = DateTimeOffset.UtcNow;
        var route = new StorageRoute
        {
            Id = Guid.NewGuid(), TeamId = teamId, DataClassTypeKey = dataClassTypeKey, CurrentRevision = 1,
            State = StorageRouteState.Draft, CreatedDate = now, CreatedBy = actorId,
            LastModifiedDate = now, LastModifiedBy = actorId,
        };
        route.Revisions.Add(Revision(route, 1, new RevisionCreation(profile.Id, selection, actorId, now)));
        _db.StorageRoute.Add(route);
        await SaveConcurrentAsync($"Storage route '{dataClassTypeKey}' already exists in this team.", cancellationToken).ConfigureAwait(false);
        return (await GetAsync(teamId, route.Id, null, StorageRouteRevisionPageLimits.DefaultPageSize, cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<StorageRouteDetail?> AppendRevisionAsync(Guid teamId, Guid actorId, AppendStorageRouteRevisionCommand command, CancellationToken cancellationToken)
    {
        var route = await _db.StorageRoute.SingleOrDefaultAsync(value => value.TeamId == teamId && value.Id == command.RouteId, cancellationToken).ConfigureAwait(false);
        if (route == null) return null;
        EnsureExpected(route, command.ExpectedXmin, command.ExpectedCurrentRevision);
        ExecuteRule(() => StorageRouteRules.EnsureRevisionAllowed(route.State));
        var selection = Selection(command.ProfileRevisionMode, command.PinnedProfileRevision);
        var profile = await RequireActiveProfileAsync(teamId, command.StorageProfileId, selection, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var next = checked(route.CurrentRevision + 1);
        _db.StorageRouteRevision.Add(Revision(route, next, new RevisionCreation(profile.Id, selection, actorId, now)));
        route.CurrentRevision = next;
        route.LastModifiedDate = now;
        route.LastModifiedBy = actorId;
        await SaveConcurrentAsync("The storage route changed before this revision could be appended.", cancellationToken).ConfigureAwait(false);
        return await GetAsync(teamId, route.Id, null, StorageRouteRevisionPageLimits.DefaultPageSize, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StorageRouteDetail?> SetStateAsync(Guid teamId, Guid actorId, SetStorageRouteStateCommand command, CancellationToken cancellationToken)
    {
        var route = await _db.StorageRoute.SingleOrDefaultAsync(value => value.TeamId == teamId && value.Id == command.RouteId, cancellationToken).ConfigureAwait(false);
        if (route == null) return null;
        EnsureExpected(route, command.ExpectedXmin, command.ExpectedCurrentRevision);
        var requested = (StorageRouteState)(int)command.State;
        ExecuteRule(() => StorageRouteRules.EnsureTransition(route.State, requested));
        if (requested == StorageRouteState.Active)
        {
            var current = await _db.StorageRouteRevision.AsNoTracking().SingleAsync(value => value.TeamId == teamId
                && value.StorageRouteId == route.Id && value.Revision == route.CurrentRevision, cancellationToken).ConfigureAwait(false);
            await RequireActiveProfileAsync(teamId, current.StorageProfileId, new ProfileSelection(current.ProfileRevisionMode, current.PinnedProfileRevision), cancellationToken).ConfigureAwait(false);
        }
        if (route.State == requested)
            return await GetAsync(teamId, route.Id, null, StorageRouteRevisionPageLimits.DefaultPageSize, cancellationToken).ConfigureAwait(false);

        route.State = requested;
        route.LastModifiedDate = DateTimeOffset.UtcNow;
        route.LastModifiedBy = actorId;
        await SaveConcurrentAsync("The storage route changed before its state could be updated.", cancellationToken).ConfigureAwait(false);
        return await GetAsync(teamId, route.Id, null, StorageRouteRevisionPageLimits.DefaultPageSize, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A NEW route may only name a data class some runtime consumer in this build reads. No consumer ever asks the
    /// routing plane for any other key, so such a route would list as configured storage and never move a byte. Only
    /// creation is gated: a row an earlier build accepted keeps its identity and can still be revised or retired.
    /// </summary>
    private void EnsureRoutedDataClass(string dataClassTypeKey)
    {
        if (_dataClasses.Get(dataClassTypeKey) != null) return;

        var known = string.Join(", ", _dataClasses.DataClasses.Select(dataClass => dataClass.TypeKey));
        throw new StorageRouteInvalidException($"No runtime consumer in this build reads data class '{dataClassTypeKey}'. Routable data classes: {known}.");
    }

    private async Task<StorageProfile> RequireActiveProfileAsync(Guid teamId, Guid profileId, ProfileSelection selection, CancellationToken cancellationToken)
    {
        var profile = await _db.StorageProfile.AsNoTracking().SingleOrDefaultAsync(value => value.TeamId == teamId && value.Id == profileId, cancellationToken).ConfigureAwait(false);
        if (profile == null || profile.State != StorageProfileState.Active)
            throw new StorageRouteInvalidException("The target storage profile must be active and owned by this team.");
        if (selection.Mode == StorageProfileRevisionMode.Pinned && !await _db.StorageProfileRevision.AsNoTracking()
            .AnyAsync(value => value.TeamId == teamId && value.StorageProfileId == profileId && value.Revision == selection.PinnedRevision, cancellationToken).ConfigureAwait(false))
            throw new StorageRouteInvalidException("The pinned storage profile revision does not exist in this team.");
        return profile;
    }

    private async Task SaveConcurrentAsync(string message, CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new StorageRouteConflictException(message, exception);
        }
        catch (Exception exception) when (IsWriteConflict(exception))
        {
            throw new StorageRouteConflictException(message, exception);
        }
    }

    internal static bool IsWriteConflict(Exception exception) => exception switch
    {
        PostgresException { SqlState: ConcurrentRouteSqlState or PostgresErrorCodes.UniqueViolation } => true,
        DbUpdateException { InnerException: { } inner } => IsWriteConflict(inner),
        _ => false,
    };

    private static ProfileSelection Selection(StorageProfileRevisionModeValue mode, int? pinnedRevision)
    {
        var mapped = (StorageProfileRevisionMode)(int)mode;
        ExecuteRule(() => StorageRouteRules.EnsureProfileSelection(mapped, pinnedRevision));
        return new ProfileSelection(mapped, pinnedRevision);
    }

    private static void EnsureExpected(StorageRoute route, uint expectedXmin, int expectedCurrentRevision)
    {
        if (route.Xmin != expectedXmin || route.CurrentRevision != expectedCurrentRevision)
            throw new StorageRouteConflictException($"Storage route version mismatch: expected xmin {expectedXmin} at revision {expectedCurrentRevision}, current xmin is {route.Xmin} at revision {route.CurrentRevision}.");
    }

    private static T ExecuteRule<T>(Func<T> action)
    {
        try { return action(); }
        catch (ArgumentException exception) { throw new StorageRouteInvalidException(exception.Message, exception); }
    }

    private static void ExecuteRule(Action action)
    {
        try { action(); }
        catch (ArgumentException exception) { throw new StorageRouteInvalidException(exception.Message, exception); }
    }

    private static StorageRouteRevision Revision(StorageRoute route, int revision, RevisionCreation input) => new()
    {
        Id = Guid.NewGuid(), TeamId = route.TeamId, StorageRouteId = route.Id, Revision = revision,
        StorageProfileId = input.ProfileId, ProfileRevisionMode = input.Selection.Mode,
        PinnedProfileRevision = input.Selection.PinnedRevision, CreatedDate = input.CreatedDate, CreatedBy = input.ActorId,
    };

    private static StorageRouteSummary Summary(StorageRoute route, StorageRouteRevision revision, string profileStableName) => new()
    {
        Id = route.Id, DataClassTypeKey = route.DataClassTypeKey, State = State(route.State), CurrentRevision = route.CurrentRevision,
        Xmin = route.Xmin, StorageProfileId = revision.StorageProfileId, StorageProfileStableName = profileStableName,
        ProfileRevisionMode = Mode(revision.ProfileRevisionMode), PinnedProfileRevision = revision.PinnedProfileRevision,
        CreatedDate = route.CreatedDate, LastModifiedDate = route.LastModifiedDate,
    };

    private static StorageRouteDetail Detail(StorageRoute route, StorageRouteRevisionDetail currentTarget, StoragePage<StorageRouteRevisionDetail> revisionPage) => new()
    {
        Id = route.Id, DataClassTypeKey = route.DataClassTypeKey, State = State(route.State), CurrentRevision = route.CurrentRevision,
        Xmin = route.Xmin, CreatedDate = route.CreatedDate, CreatedBy = route.CreatedBy,
        LastModifiedDate = route.LastModifiedDate, LastModifiedBy = route.LastModifiedBy,
        CurrentTarget = currentTarget, RevisionPage = revisionPage,
    };

    private static StorageRouteRevisionDetail RevisionDetail(StorageRouteRevision revision, string profileStableName) => new()
    {
        Id = revision.Id, Revision = revision.Revision, StorageProfileId = revision.StorageProfileId,
        StorageProfileStableName = profileStableName, ProfileRevisionMode = Mode(revision.ProfileRevisionMode),
        PinnedProfileRevision = revision.PinnedProfileRevision, CreatedDate = revision.CreatedDate, CreatedBy = revision.CreatedBy,
    };

    private static StorageRouteStateValue State(StorageRouteState state) => (StorageRouteStateValue)(int)state;
    private static StorageProfileRevisionModeValue Mode(StorageProfileRevisionMode mode) => (StorageProfileRevisionModeValue)(int)mode;
    private sealed record ProfileSelection(StorageProfileRevisionMode Mode, int? PinnedRevision);
    private sealed record RevisionCreation(Guid ProfileId, ProfileSelection Selection, Guid ActorId, DateTimeOffset CreatedDate);
}
