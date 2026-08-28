using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles.Exceptions;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Dtos.Storage;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Profiles;

/// <summary>
/// Team-admin control plane for the append-only storage-profile ledger. It deliberately has no dependency on an
/// artifact store, driver factory, workflow, agent, harness, or completion authority.
/// </summary>
public sealed class StorageProfileService : IStorageProfileService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IStorageProviderModuleCatalog _catalog;

    public StorageProfileService(CodeSpaceDbContext db, IStorageProviderModuleCatalog catalog)
    {
        _db = db;
        _catalog = catalog;
    }

    public async Task<IReadOnlyList<StorageProfileSummary>> ListAsync(Guid teamId, CancellationToken cancellationToken)
    {
        var rows = await (
            from profile in _db.StorageProfile.AsNoTracking()
            join revision in _db.StorageProfileRevision.AsNoTracking()
                on new { profile.TeamId, StorageProfileId = profile.Id, Revision = profile.CurrentRevision }
                equals new { revision.TeamId, revision.StorageProfileId, revision.Revision }
            join health in _db.StorageProfileHealth.AsNoTracking()
                on new { profile.TeamId, StorageProfileId = profile.Id } equals new { health.TeamId, health.StorageProfileId } into healthRows
            from health in healthRows.DefaultIfEmpty()
            where profile.TeamId == teamId
            orderby profile.StableName, profile.Id
            select new { Profile = profile, revision.ProviderTypeKey, Health = health })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(row => Summary(row.Profile, row.ProviderTypeKey, row.Health)).ToList();
    }

    public async Task<StoragePage<StorageProfileSummary>> ListPageAsync(Guid teamId, string? cursor, int limit, CancellationToken cancellationToken)
    {
        var keyset = StorageSettingsCursor.Decode(cursor);
        var take = Math.Clamp(limit, 1, StoragePageLimits.MaxPageSize);
        var query =
            from profile in _db.StorageProfile.AsNoTracking()
            join revision in _db.StorageProfileRevision.AsNoTracking()
                on new { profile.TeamId, StorageProfileId = profile.Id, Revision = profile.CurrentRevision }
                equals new { revision.TeamId, revision.StorageProfileId, revision.Revision }
            join health in _db.StorageProfileHealth.AsNoTracking()
                on new { profile.TeamId, StorageProfileId = profile.Id } equals new { health.TeamId, health.StorageProfileId } into healthRows
            from health in healthRows.DefaultIfEmpty()
            where profile.TeamId == teamId
            select new { Profile = profile, revision.ProviderTypeKey, Health = health };
        if (keyset is { } after)
            query = query.Where(row => string.Compare(row.Profile.StableName, after.StableName) > 0
                || (row.Profile.StableName == after.StableName && row.Profile.Id.CompareTo(after.Id) > 0));

        var rows = await query.OrderBy(row => row.Profile.StableName).ThenBy(row => row.Profile.Id).Take(take + 1)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var hasMore = rows.Count > take;
        var page = hasMore ? rows.GetRange(0, take) : rows;
        return new StoragePage<StorageProfileSummary>
        {
            Items = page.Select(row => Summary(row.Profile, row.ProviderTypeKey, row.Health)).ToList(),
            NextCursor = hasMore ? new StorageSettingsCursor(page[^1].Profile.StableName, page[^1].Profile.Id).Encode() : null,
        };
    }

    public async Task<StorageProfileDetail?> GetAsync(Guid teamId, Guid profileId, CancellationToken cancellationToken)
    {
        var profile = await _db.StorageProfile.AsNoTracking()
            .Include(value => value.Revisions)
            .SingleOrDefaultAsync(value => value.TeamId == teamId && value.Id == profileId, cancellationToken)
            .ConfigureAwait(false);
        return profile == null ? null : Detail(profile);
    }

    public async Task<StorageProfileDetail> CreateAsync(Guid teamId, Guid actorId, CreateStorageProfileCommand command, CancellationToken cancellationToken)
    {
        var stableName = ExecuteRule(() => StorageProfileRules.NormalizeStableName(command.StableName));
        if (await _db.StorageProfile.AsNoTracking().AnyAsync(profile => profile.TeamId == teamId && profile.StableName == stableName, cancellationToken).ConfigureAwait(false))
            throw new StorageProfileConflictException($"Storage profile '{stableName}' already exists in this team.");

        var prepared = await PrepareRevisionAsync(teamId, command.ProviderTypeKey, command.NonSecretConfig, command.CredentialRef, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var profile = new StorageProfile
        {
            Id = Guid.NewGuid(), TeamId = teamId, StableName = stableName, CurrentRevision = 1,
            State = StorageProfileState.Draft, CreatedDate = now, CreatedBy = actorId,
            LastModifiedDate = now, LastModifiedBy = actorId,
        };
        profile.Revisions.Add(Revision(profile, 1, actorId, now, prepared));
        _db.StorageProfile.Add(profile);

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            throw new StorageProfileConflictException($"Storage profile '{stableName}' already exists in this team.", exception);
        }

        return Detail(profile);
    }

    public async Task<StorageProfileDetail?> AppendRevisionAsync(Guid teamId, Guid actorId, AppendStorageProfileRevisionCommand command, CancellationToken cancellationToken)
    {
        var profile = await _db.StorageProfile.SingleOrDefaultAsync(value => value.TeamId == teamId && value.Id == command.ProfileId, cancellationToken).ConfigureAwait(false);
        if (profile == null) return null;
        EnsureExpected(profile, command.ExpectedXmin, command.ExpectedCurrentRevision);
        ExecuteRule(() => StorageProfileRules.EnsureRevisionAllowed(profile.State));

        var prepared = await PrepareRevisionAsync(teamId, command.ProviderTypeKey, command.NonSecretConfig, command.CredentialRef, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var next = checked(profile.CurrentRevision + 1);
        _db.StorageProfileRevision.Add(Revision(profile, next, actorId, now, prepared));
        profile.CurrentRevision = next;
        profile.LastModifiedDate = now;
        profile.LastModifiedBy = actorId;

        await SaveConcurrentAsync("The storage profile changed before this revision could be appended.", cancellationToken).ConfigureAwait(false);
        return await GetAsync(teamId, profile.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StorageProfileDetail?> SetStateAsync(Guid teamId, Guid actorId, SetStorageProfileStateCommand command, CancellationToken cancellationToken)
    {
        var profile = await _db.StorageProfile.SingleOrDefaultAsync(value => value.TeamId == teamId && value.Id == command.ProfileId, cancellationToken).ConfigureAwait(false);
        if (profile == null) return null;
        EnsureExpected(profile, command.ExpectedXmin, command.ExpectedCurrentRevision);

        var requested = (StorageProfileState)(int)command.State;
        ExecuteRule(() => StorageProfileRules.EnsureTransition(profile.State, requested));
        if (profile.State == requested) return await GetAsync(teamId, profile.Id, cancellationToken).ConfigureAwait(false);

        await EnsureRetirementReleasedAsync(teamId, profile.Id, requested, cancellationToken).ConfigureAwait(false);

        profile.State = requested;
        profile.LastModifiedDate = DateTimeOffset.UtcNow;
        profile.LastModifiedBy = actorId;
        await SaveConcurrentAsync("The storage profile changed before its state could be updated.", cancellationToken).ConfigureAwait(false);
        return await GetAsync(teamId, profile.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Retirement is the one irreversible profile transition — <see cref="StorageProfileRules.EnsureTransition"/> gives
    /// a retired profile no way back — so it must not be reachable while something still names the profile. Disable is
    /// deliberately unguarded: it is reversible, it no longer strands reads, and quiescing writes with it is exactly how
    /// an operator drains a profile before retiring it.
    /// </summary>
    private async Task EnsureRetirementReleasedAsync(Guid teamId, Guid profileId, StorageProfileState requested, CancellationToken cancellationToken)
    {
        if (requested != StorageProfileState.Retired) return;

        var routes = await CountActiveRoutesAsync(teamId, profileId, cancellationToken).ConfigureAwait(false);
        if (routes > 0)
            throw new StorageProfileConflictException($"Storage profile cannot be retired while {routes} active storage route(s) still target it. Repoint or disable those routes first.");

        var locations = await CountAvailableLocationsAsync(teamId, profileId, cancellationToken).ConfigureAwait(false);
        if (locations > 0)
            throw new StorageProfileConflictException($"Storage profile cannot be retired while {locations} stored artifact location(s) still live under it. Migrate or delete those artifacts first.");
    }

    private Task<int> CountActiveRoutesAsync(Guid teamId, Guid profileId, CancellationToken cancellationToken) =>
        (from route in _db.StorageRoute.AsNoTracking()
         join revision in _db.StorageRouteRevision.AsNoTracking()
             on new { route.TeamId, StorageRouteId = route.Id, Revision = route.CurrentRevision }
             equals new { revision.TeamId, revision.StorageRouteId, revision.Revision }
         where route.TeamId == teamId && route.State == StorageRouteState.Active && revision.StorageProfileId == profileId
         select route.Id)
        .CountAsync(cancellationToken);

    private Task<int> CountAvailableLocationsAsync(Guid teamId, Guid profileId, CancellationToken cancellationToken) =>
        (from location in _db.ArtifactLocation.AsNoTracking()
         join revision in _db.StorageProfileRevision.AsNoTracking()
             on new { location.TeamId, Id = location.StorageProfileRevisionId }
             equals new { revision.TeamId, revision.Id }
         where location.TeamId == teamId && revision.StorageProfileId == profileId && location.State == ArtifactLocationState.Available
         select location.Id)
        .CountAsync(cancellationToken);

    private async Task<PreparedRevision> PrepareRevisionAsync(Guid teamId, string providerTypeKey, JsonElement config, string? credentialRef, CancellationToken cancellationToken)
    {
        IStorageProviderModule module;
        try
        {
            module = _catalog.Require(providerTypeKey);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw new StorageProfileInvalidException(exception.Message, exception);
        }

        ExecuteRule(() => StorageProfileRules.ValidateConfig(config, module.ConfigSchema, module.SecretSchema));
        var canonicalConfig = ExecuteRule(() => StorageProfileRules.CanonicalJson(config));

        // The provider is asked about the canonical form because that is the form persisted below, and the form the
        // snapshot resolver re-canonicalizes before handing it to the same provider at activation.
        var canonicalElement = Parse(canonicalConfig);
        ExecuteRule(() => module.EnsureConfigurationReadable(canonicalElement));

        var normalizedCredentialRef = await ValidateCredentialAsync(teamId, module.TypeKey, credentialRef, cancellationToken).ConfigureAwait(false);
        var namespaceConfig = module.GetNamespaceConfiguration(canonicalElement);
        if (namespaceConfig.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"Storage provider '{module.TypeKey}' namespace configuration projection must be a JSON object.");
        var fingerprint = ExecuteRule(() => StorageProfileRules.NamespaceFingerprint(module.TypeKey, namespaceConfig));
        return new PreparedRevision(module.TypeKey, canonicalConfig, normalizedCredentialRef, fingerprint);
    }

    private async Task<string?> ValidateCredentialAsync(Guid teamId, string providerTypeKey, string? credentialRef, CancellationToken cancellationToken)
    {
        if (credentialRef == null) return null;
        if (!StorageProfileRules.TryParseCredentialRef(credentialRef, out var reference))
            throw new StorageProfileInvalidException("CredentialRef must use the structured form 'db:<uuid>:<positive-version>'; secret values and environment references are not accepted.");

        var credential = await (
            from identity in _db.StorageCredential.AsNoTracking()
            join revision in _db.StorageCredentialRevision.AsNoTracking()
                on new { identity.TeamId, StorageCredentialId = identity.Id }
                equals new { revision.TeamId, revision.StorageCredentialId }
            where identity.TeamId == teamId && identity.Id == reference.Id && identity.State == StorageCredentialState.Active && revision.Revision == reference.Revision
            select new { revision.ProviderTypeKey })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (credential == null) throw new StorageProfileInvalidException("CredentialRef does not identify an active storage-credential revision owned by this team.");
        if (!string.Equals(credential.ProviderTypeKey, providerTypeKey, StringComparison.Ordinal))
            throw new StorageProfileInvalidException($"CredentialRef provider '{credential.ProviderTypeKey}' does not match storage profile provider '{providerTypeKey}'.");
        return reference.Canonical;
    }

    private async Task SaveConcurrentAsync(string message, CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new StorageProfileConflictException(message, exception);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            throw new StorageProfileConflictException(message, exception);
        }
    }

    private static void EnsureExpected(StorageProfile profile, uint expectedXmin, int expectedCurrentRevision)
    {
        if (profile.Xmin != expectedXmin || profile.CurrentRevision != expectedCurrentRevision)
            throw new StorageProfileConflictException($"Storage profile version mismatch: expected xmin {expectedXmin} at revision {expectedCurrentRevision}, current xmin is {profile.Xmin} at revision {profile.CurrentRevision}.");
    }

    private static T ExecuteRule<T>(Func<T> action)
    {
        try { return action(); }
        catch (ArgumentException exception) { throw new StorageProfileInvalidException(exception.Message, exception); }
    }

    private static void ExecuteRule(Action action)
    {
        try { action(); }
        catch (ArgumentException exception) { throw new StorageProfileInvalidException(exception.Message, exception); }
    }

    private static StorageProfileRevision Revision(StorageProfile profile, int revision, Guid actorId, DateTimeOffset now, PreparedRevision prepared) => new()
    {
        Id = Guid.NewGuid(), TeamId = profile.TeamId, StorageProfileId = profile.Id, Revision = revision,
        ProviderTypeKey = prepared.ProviderTypeKey, NonSecretConfigJson = prepared.NonSecretConfigJson,
        CredentialRef = prepared.CredentialRef, NamespaceFingerprint = prepared.NamespaceFingerprint,
        CreatedDate = now, CreatedBy = actorId,
    };

    private static StorageProfileSummary Summary(StorageProfile profile, string providerTypeKey, StorageProfileHealth? health) => new()
    {
        Id = profile.Id, StableName = profile.StableName, State = State(profile.State), CurrentRevision = profile.CurrentRevision,
        Xmin = profile.Xmin, ProviderTypeKey = providerTypeKey, CreatedDate = profile.CreatedDate, LastModifiedDate = profile.LastModifiedDate,
        Health = Health(health),
    };

    /// <summary>
    /// Null when nothing has ever probed this destination, and that is reported as null rather than smoothed into a
    /// neutral-looking status: "nobody has checked" and "checked and working" are different facts, and only one of
    /// them is a reason to trust the destination.
    /// </summary>
    private static StorageProfileHealthSummary? Health(StorageProfileHealth? health) => health == null ? null : new StorageProfileHealthSummary
    {
        Status = health.Status, WriteVerified = health.WriteVerified, ProfileRevision = health.ProfileRevision,
        FailureStage = health.FailureStage, FailureCode = health.FailureCode,
        LatencyMilliseconds = health.LatencyMs, ObservedAt = health.ObservedAt,
    };

    private static StorageProfileDetail Detail(StorageProfile profile) => new()
    {
        Id = profile.Id, StableName = profile.StableName, State = State(profile.State), CurrentRevision = profile.CurrentRevision,
        Xmin = profile.Xmin, CreatedDate = profile.CreatedDate, CreatedBy = profile.CreatedBy,
        LastModifiedDate = profile.LastModifiedDate, LastModifiedBy = profile.LastModifiedBy,
        Revisions = profile.Revisions.OrderByDescending(revision => revision.Revision).Select(RevisionDetail).ToList(),
    };

    private static StorageProfileRevisionDetail RevisionDetail(StorageProfileRevision revision) => new()
    {
        Id = revision.Id, Revision = revision.Revision, ProviderTypeKey = revision.ProviderTypeKey,
        NonSecretConfig = Parse(revision.NonSecretConfigJson), CredentialRef = revision.CredentialRef,
        NamespaceFingerprint = revision.NamespaceFingerprint, CreatedDate = revision.CreatedDate, CreatedBy = revision.CreatedBy,
    };

    private static StorageProfileStateValue State(StorageProfileState state) => (StorageProfileStateValue)(int)state;

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static bool IsUniqueViolation(DbUpdateException exception) => exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private sealed record PreparedRevision(string ProviderTypeKey, string NonSecretConfigJson, string? CredentialRef, string NamespaceFingerprint);
}
