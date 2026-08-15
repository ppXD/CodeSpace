using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.Core.Settings;
using CodeSpace.Messages.Constants;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CodeSpace.Core.Services.Agents.AgentRunLogging;

/// <summary>
/// Gives an unconfigured team one real Settings-visible local route only after the deployment explicitly qualifies its
/// local-rwx namespace as shared. This is a missing-only bootstrap, not a routing fallback: an existing route in any
/// lifecycle state or any collision on the reserved profile name remains authoritative, and later Settings revisions can
/// move the data class to any registered cloud provider without changing Agent Run capture.
/// </summary>
public sealed class AgentRunLogStorageReadiness : IAgentRunLogStorageReadiness
{
    internal const string DefaultProfileStableName = "codespace-agent-run-log-default";
    private const int AdvisoryLockNamespace = 117;
    private readonly CodeSpaceDbContext _db;
    private readonly TimeProvider _clock;

    public AgentRunLogStorageReadiness(CodeSpaceDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task EnsureDefaultRouteAsync(Guid teamId, CancellationToken cancellationToken)
    {
        if (teamId == Guid.Empty || !RuntimeSettings.Current.ArtifactLocalRwxShared) return;

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({teamId.ToString()}, {AdvisoryLockNamespace}))", cancellationToken).ConfigureAwait(false);
            if (!await _db.Team.AsNoTracking().AnyAsync(team => team.Id == teamId, cancellationToken).ConfigureAwait(false)
                || await _db.StorageRoute.AsNoTracking().AnyAsync(route => route.TeamId == teamId && route.DataClassTypeKey == AgentRunLogStorageResolver.DataClassTypeKey, cancellationToken).ConfigureAwait(false))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var rootPath = DurableRoots.ArtifactStore(RuntimeSettings.Current.ArtifactStoreDirectory);
            var canonicalConfig = CanonicalConfig(rootPath);
            if (await _db.StorageProfile.AsNoTracking().AnyAsync(
                    value => value.TeamId == teamId && value.StableName == DefaultProfileStableName, cancellationToken).ConfigureAwait(false))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var profile = BuildProfile(teamId, canonicalConfig, _clock.GetUtcNow());
            _db.StorageProfile.Add(profile);
            var route = BuildRoute(teamId, profile.Id, _clock.GetUtcNow());
            _db.StorageRoute.Add(route);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            route.State = StorageRouteState.Active;
            route.LastModifiedDate = _clock.GetUtcNow();
            route.LastModifiedBy = SystemUsers.SeederId;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsUniqueViolation(exception))
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            _db.ChangeTracker.Clear();
        }
    }

    private static StorageProfile BuildProfile(Guid teamId, string canonicalConfig, DateTimeOffset now)
    {
        var profile = new StorageProfile
        {
            Id = Guid.NewGuid(), TeamId = teamId, StableName = DefaultProfileStableName, CurrentRevision = 1,
            State = StorageProfileState.Active, CreatedDate = now, CreatedBy = SystemUsers.SeederId,
            LastModifiedDate = now, LastModifiedBy = SystemUsers.SeederId,
        };
        using var document = JsonDocument.Parse(canonicalConfig);
        profile.Revisions.Add(new StorageProfileRevision
        {
            Id = Guid.NewGuid(), TeamId = teamId, StorageProfileId = profile.Id, Revision = 1,
            ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey, NonSecretConfigJson = canonicalConfig,
            CredentialRef = null, NamespaceFingerprint = StorageProfileRules.NamespaceFingerprint(LocalRwxArtifactStorageDriverFactory.TypeKey, document.RootElement),
            CreatedDate = now, CreatedBy = SystemUsers.SeederId,
        });
        return profile;
    }

    private static StorageRoute BuildRoute(Guid teamId, Guid profileId, DateTimeOffset now)
    {
        var route = new StorageRoute
        {
            Id = Guid.NewGuid(), TeamId = teamId, DataClassTypeKey = AgentRunLogStorageResolver.DataClassTypeKey,
            CurrentRevision = 1, State = StorageRouteState.Draft, CreatedDate = now, CreatedBy = SystemUsers.SeederId,
            LastModifiedDate = now, LastModifiedBy = SystemUsers.SeederId,
        };
        route.Revisions.Add(new StorageRouteRevision
        {
            Id = Guid.NewGuid(), TeamId = teamId, StorageRouteId = route.Id, Revision = 1, StorageProfileId = profileId,
            ProfileRevisionMode = StorageProfileRevisionMode.CurrentAtWrite, PinnedProfileRevision = null,
            CreatedDate = now, CreatedBy = SystemUsers.SeederId,
        });
        return route;
    }

    private static string CanonicalConfig(string rootPath)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { rootPath }));
        return StorageProfileRules.CanonicalJson(document.RootElement);
    }

    private static bool IsUniqueViolation(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }) return true;
        return false;
    }
}
