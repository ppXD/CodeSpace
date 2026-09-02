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
    private readonly Providers.IStorageProviderModuleCatalog _modules;
    private readonly Runtime.IStorageProfileProbeService _probe;

    public StorageRouteService(CodeSpaceDbContext db, IRoutedDataClassCatalog dataClasses, Providers.IStorageProviderModuleCatalog modules, Runtime.IStorageProfileProbeService probe)
    {
        _db = db;
        _dataClasses = dataClasses;
        _modules = modules;
        _probe = probe;
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

    public async Task<StorageRouteSummary?> GetByDataClassAsync(Guid teamId, string dataClassTypeKey, CancellationToken cancellationToken)
    {
        var normalized = ExecuteRule(() => StorageRouteRules.NormalizeDataClassTypeKey(dataClassTypeKey));
        var row = await (
            from route in _db.StorageRoute.AsNoTracking()
            join revision in _db.StorageRouteRevision.AsNoTracking()
                on new { route.TeamId, StorageRouteId = route.Id, Revision = route.CurrentRevision }
                equals new { revision.TeamId, revision.StorageRouteId, revision.Revision }
            join profile in _db.StorageProfile.AsNoTracking()
                on new { revision.TeamId, revision.StorageProfileId }
                equals new { profile.TeamId, StorageProfileId = profile.Id }
            where route.TeamId == teamId && route.DataClassTypeKey == normalized
            select new { Route = route, Revision = revision, ProfileStableName = profile.StableName })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return row == null ? null : Summary(row.Route, row.Revision, row.ProfileStableName);
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

        // An Active route moved onto another profile becomes a writer of that profile's head the moment this commits,
        // so this is the same door as activation and takes the same lock. A Draft route becomes nothing; it pays a
        // lock nobody contends rather than earning a branch that asks which state it was in.
        await using var owned = await StorageProfileHeadLock.TakeAsync(_db.Database, command.StorageProfileId, cancellationToken).ConfigureAwait(false);
        var profile = await RequireActiveProfileAsync(teamId, command.StorageProfileId, selection, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var next = checked(route.CurrentRevision + 1);
        _db.StorageRouteRevision.Add(Revision(route, next, new RevisionCreation(profile.Id, selection, actorId, now)));
        route.CurrentRevision = next;
        route.LastModifiedDate = now;
        route.LastModifiedBy = actorId;
        await SaveConcurrentAsync("The storage route changed before this revision could be appended.", cancellationToken).ConfigureAwait(false);
        var detail = await GetAsync(teamId, route.Id, null, StorageRouteRevisionPageLimits.DefaultPageSize, cancellationToken).ConfigureAwait(false);

        if (owned != null) await owned.CommitAsync(cancellationToken).ConfigureAwait(false);

        return detail;
    }

    public async Task<StorageRouteDetail?> SetStateAsync(Guid teamId, Guid actorId, SetStorageRouteStateCommand command, CancellationToken cancellationToken)
    {
        var route = await _db.StorageRoute.SingleOrDefaultAsync(value => value.TeamId == teamId && value.Id == command.RouteId, cancellationToken).ConfigureAwait(false);
        if (route == null) return null;
        EnsureExpected(route, command.ExpectedXmin, command.ExpectedCurrentRevision);
        var requested = (StorageRouteState)(int)command.State;
        ExecuteRule(() => StorageRouteRules.EnsureTransition(route.State, requested));

        if (requested == StorageRouteState.Active) return await ActivateAsync(teamId, actorId, route, cancellationToken).ConfigureAwait(false);

        if (route.State == requested)
            return await GetAsync(teamId, route.Id, null, StorageRouteRevisionPageLimits.DefaultPageSize, cancellationToken).ConfigureAwait(false);

        return await ApplyStateAsync(teamId, actorId, route, requested, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The Active transition — the door that turns this route into a writer of the profile head it names.
    ///
    /// <para>The head is read twice, on purpose. The first read refuses an ineligible profile BEFORE the destination
    /// probe, so an operator gets the reason they can act on rather than a write failure at a destination that was
    /// never going to take one. The second read asks the identical question under
    /// <see cref="StorageProfileHeadLock"/> and holds it through the write, because the first answer is a snapshot: a
    /// profile revision repointing this very head can commit between the two, and then both ends have passed their
    /// own guard and the forbidden state is committed.</para>
    ///
    /// <para>The probe stays OUTSIDE that transaction. What it observed is worth keeping even when the activation it
    /// refused is rolled back — an operator who was refused can see WHY on the profile afterwards without re-running
    /// anything, and the recorded observation is the only trace that they looked.</para>
    /// </summary>
    private async Task<StorageRouteDetail?> ActivateAsync(Guid teamId, Guid actorId, StorageRoute route, CancellationToken cancellationToken)
    {
        var current = await _db.StorageRouteRevision.AsNoTracking().SingleAsync(value => value.TeamId == teamId
            && value.StorageRouteId == route.Id && value.Revision == route.CurrentRevision, cancellationToken).ConfigureAwait(false);
        var selection = new ProfileSelection(current.ProfileRevisionMode, current.PinnedProfileRevision);

        await RequireActiveProfileAsync(teamId, current.StorageProfileId, selection, cancellationToken).ConfigureAwait(false);

        if (route.State == StorageRouteState.Active)
            return await GetAsync(teamId, route.Id, null, StorageRouteRevisionPageLimits.DefaultPageSize, cancellationToken).ConfigureAwait(false);

        await ProveDestinationWritableAsync(teamId, current, cancellationToken).ConfigureAwait(false);

        await using var owned = await StorageProfileHeadLock.TakeAsync(_db.Database, current.StorageProfileId, cancellationToken).ConfigureAwait(false);
        await RequireActiveProfileAsync(teamId, current.StorageProfileId, selection, cancellationToken).ConfigureAwait(false);

        var detail = await ApplyStateAsync(teamId, actorId, route, StorageRouteState.Active, cancellationToken).ConfigureAwait(false);

        if (owned != null) await owned.CommitAsync(cancellationToken).ConfigureAwait(false);

        return detail;
    }

    private async Task<StorageRouteDetail?> ApplyStateAsync(Guid teamId, Guid actorId, StorageRoute route, StorageRouteState requested, CancellationToken cancellationToken)
    {
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

/// <summary>
    /// Writes and discards one real object at the destination this route is about to bind, BEFORE it binds it.
    ///
    /// <para>Activation is a one-way door: <c>StorageRouteRules.EnsureTransition</c> refuses every transition back to
    /// Draft, Retired is terminal, and a route cannot be deleted. Until now the entire gate was a database read
    /// asserting the profile row says Active — no driver opened, no credential resolved, nothing written — so a route
    /// pointing at a nonexistent bucket, a mistyped endpoint or a key that was never valid reached Active and started
    /// binding writes. For <c>workflow-artifact/v1</c> this is the ONLY path: its data class declares a local home, so
    /// the deployment-default materializer's probed adoption may never take it automatically.</para>
    ///
    /// <para>Only on the actual transition. An idempotent re-activation of an already-Active route must stay a no-op:
    /// failing it during a transient outage would make a caller's retry the thing that breaks.</para>
    ///
    /// <para>Only <c>Available</c> passes, and a retryable failure is refused like any other — activating onto a
    /// destination that is unreachable right now is exactly the mistake this exists to prevent. The message carries the
    /// provider's own stage and code so an operator is told which end to fix.</para>
    /// </summary>
    private async Task ProveDestinationWritableAsync(Guid teamId, StorageRouteRevision revision, CancellationToken cancellationToken)
    {
        var result = await _probe.ProbeAsync(
            new Runtime.StorageProfileProbeRequest(teamId, revision.StorageProfileId, revision.PinnedProfileRevision, VerifyWriteAccess: true, Initialize: true), cancellationToken)
            .ConfigureAwait(false);

        if (result.Status == StorageProfileProbeStatusValue.Available) return;

        var reason = result.Failure is { } failure ? $" ({failure.Stage}/{failure.Code})" : string.Empty;
        var retry = result.Failure?.Retryable == true ? " The destination reported this as temporary, so activating again later may succeed." : string.Empty;

        throw new StorageRouteInvalidException(
            $"The destination did not accept a write, so this route was not activated{reason}. Activating a route cannot be undone, so it is refused rather than bound to a destination that is not taking bytes.{retry}");
    }

    private async Task<StorageProfile> RequireActiveProfileAsync(Guid teamId, Guid profileId, ProfileSelection selection, CancellationToken cancellationToken)
    {
        var profile = await _db.StorageProfile.AsNoTracking().SingleOrDefaultAsync(value => value.TeamId == teamId && value.Id == profileId, cancellationToken).ConfigureAwait(false);
        if (profile == null || profile.State != StorageProfileState.Active)
            throw new StorageRouteInvalidException("The target storage profile must be active and owned by this team.");
        if (selection.Mode == StorageProfileRevisionMode.Pinned && !await _db.StorageProfileRevision.AsNoTracking()
            .AnyAsync(value => value.TeamId == teamId && value.StorageProfileId == profileId && value.Revision == selection.PinnedRevision, cancellationToken).ConfigureAwait(false))
            throw new StorageRouteInvalidException("The pinned storage profile revision does not exist in this team.");

        await EnsureProviderAcceptsBytesAsync(teamId, profileId, selection.PinnedRevision ?? profile.CurrentRevision, cancellationToken).ConfigureAwait(false);

        return profile;
    }

    /// <summary>
    /// Refuses a profile whose PROVIDER TYPE takes no bytes at all, at every point a route names one — creation, a new
    /// revision, and activation, which all pass through here.
    ///
    /// <para>The neighbouring gate, <see cref="ProveDestinationWritableAsync"/>, asks the destination whether it is
    /// taking bytes right now. That question cannot answer this one: its refusal is a temporary fact about one
    /// destination, and it is only asked on the Draft→Active transition — so an already-Active route re-pointed at
    /// such a provider by a new revision would never be asked at all, and would fail at the first artifact write with
    /// no operator watching. A provider carrying <see cref="Providers.IStorageProviderAcceptsNoNewBytes"/> can never
    /// come good, so the refusal belongs here, by declaration, where the operator is still standing.</para>
    ///
    /// <para>This is the ROUTE-side reading of one rule. The profile-side reading, which is where the fact is decided,
    /// is <c>StorageProfileService.EnsureWritersKeepAWritableProviderAsync</c>: it refuses a profile REVISION that
    /// would repoint a profile an Active route already writes through. Neither end suffices alone — a route names a
    /// profile and a profile names a provider, so either can be moved onto the other.</para>
    ///
    /// <para>Two readings of one rule are only one rule while they are serialized: this read is a snapshot of rows the
    /// other end is free to change. Every caller that can bind an ACTIVE writer — <see cref="ActivateAsync"/> and
    /// <see cref="AppendRevisionAsync"/> — therefore holds <see cref="StorageProfileHeadLock"/> on the named profile
    /// across this read and its own commit. <see cref="CreateAsync"/> does not, and needs not: a new route is born
    /// Draft, so nothing it commits can be the state the rule forbids.</para>
    /// </summary>
    private async Task EnsureProviderAcceptsBytesAsync(Guid teamId, Guid profileId, int revision, CancellationToken cancellationToken)
    {
        var providerTypeKey = await _db.StorageProfileRevision.AsNoTracking()
            .Where(value => value.TeamId == teamId && value.StorageProfileId == profileId && value.Revision == revision)
            .Select(value => value.ProviderTypeKey).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(providerTypeKey) || _modules.Get(providerTypeKey) is not Providers.IStorageProviderAcceptsNoNewBytes) return;

        throw new StorageRouteInvalidException($"Storage provider '{providerTypeKey}' accepts no new bytes, so no data class can be routed to it. Its objects were placed by an earlier process and it exists to be read; a route bound to it would fail every write.");
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
