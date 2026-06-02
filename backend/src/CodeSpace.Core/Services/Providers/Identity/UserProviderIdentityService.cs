using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Providers.Identity;

public sealed class UserProviderIdentityService : IUserProviderIdentityService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly ICurrentTeam _currentTeam;
    private readonly IPayloadEncryptor _encryptor;
    private readonly ICredentialPayloadSerializer _serializer;
    private readonly IProviderRegistry _registry;

    public UserProviderIdentityService(CodeSpaceDbContext db, ICurrentUser currentUser, ICurrentTeam currentTeam, IPayloadEncryptor encryptor, ICredentialPayloadSerializer serializer, IProviderRegistry registry)
    {
        _db = db;
        _currentUser = currentUser;
        _currentTeam = currentTeam;
        _encryptor = encryptor;
        _serializer = serializer;
        _registry = registry;
    }

    public async Task<UserProviderIdentitySummary> LinkByPatAsync(Guid providerInstanceId, string accessToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) throw new InvalidOperationException("Access token must not be empty.");

        var userId = _currentUser.Id!.Value;
        var teamId = _currentTeam.Id!.Value;

        var instance = await LoadInstanceAsync(providerInstanceId, teamId, cancellationToken).ConfigureAwait(false);

        var credential = BuildPersonalCredential(instance, userId, teamId, accessToken);
        var profile = await ProbeOrThrowAsync(instance, credential, cancellationToken).ConfigureAwait(false);

        credential.DisplayName = $"{instance.Provider} · {profile.AuthenticatedUserName}";

        // Persist the token's real granted scopes (the probe reads them live) so capability warnings
        // reflect this PAT — same as the OAuth flow stores token.GrantedScopes. Null when the provider
        // couldn't expose them; the capability check treats that as "unknown", never a false warning.
        credential.Scopes = profile.GrantedScopes?.ToList();
        await ReplaceExistingLinkAsync(userId, providerInstanceId, cancellationToken).ConfigureAwait(false);

        await _db.Credential.AddAsync(credential, cancellationToken).ConfigureAwait(false);
        var identity = BuildIdentity(userId, providerInstanceId, credential.Id, profile);
        await _db.UserProviderIdentity.AddAsync(identity, cancellationToken).ConfigureAwait(false);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToSummary(identity, instance.Provider, credential.Status);
    }

    public async Task<IReadOnlyList<UserProviderIdentitySummary>> ListMineAsync(CancellationToken cancellationToken)
    {
        var userId = _currentUser.Id!.Value;

        return await _db.UserProviderIdentity.AsNoTracking()
            .Where(i => i.UserId == userId && i.DeletedDate == null)
            .OrderByDescending(i => i.CreatedDate)
            .Select(i => new UserProviderIdentitySummary
            {
                Id = i.Id,
                ProviderInstanceId = i.ProviderInstanceId,
                Provider = i.ProviderInstance.Provider,
                ProviderUsername = i.ProviderUsername,
                ProviderUserId = i.ProviderUserId,
                AvatarUrl = i.AvatarUrl,
                CredentialStatus = i.Credential.Status,
                CreatedDate = i.CreatedDate
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UnlinkAsync(Guid identityId, CancellationToken cancellationToken)
    {
        var userId = _currentUser.Id!.Value;

        var identity = await _db.UserProviderIdentity
            .Include(i => i.Credential)
            .SingleOrDefaultAsync(i => i.Id == identityId && i.UserId == userId && i.DeletedDate == null, cancellationToken).ConfigureAwait(false);

        if (identity == null) return;

        var now = DateTimeOffset.UtcNow;
        identity.DeletedDate = now;

        // Clear the token material + soft-delete the backing credential — an unlinked identity must
        // leave nothing usable at rest.
        identity.Credential.DeletedDate = now;
        identity.Credential.Status = CredentialStatus.Revoked;
        identity.Credential.EncryptedPayload = string.Empty;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task EnsureIdentityForCredentialAsync(ProviderInstance instance, Credential credential, Guid userId, CancellationToken cancellationToken)
    {
        var profile = await ProbeOrThrowAsync(instance, credential, cancellationToken).ConfigureAwait(false);

        var existing = await _db.UserProviderIdentity
            .SingleOrDefaultAsync(i => i.UserId == userId && i.ProviderInstanceId == instance.Id && i.DeletedDate == null, cancellationToken).ConfigureAwait(false);

        // Already linked on this instance — re-point the single identity at the freshly connected
        // credential so "act as me" uses the latest token, WITHOUT revoking the previous one (the
        // user manages credentials on the Personal tab). Keeps one live identity per (user, instance),
        // consistent with the partial unique index. The OAuth flow's UnitOfWork persists this.
        if (existing != null)
        {
            existing.CredentialId = credential.Id;
            existing.ProviderUserId = profile.AuthenticatedUserExternalId ?? existing.ProviderUserId;
            existing.ProviderUsername = profile.AuthenticatedUserName ?? existing.ProviderUsername;
            return;
        }

        var identity = BuildIdentity(userId, instance.Id, credential.Id, profile);
        await _db.UserProviderIdentity.AddAsync(identity, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProviderInstance> LoadInstanceAsync(Guid providerInstanceId, Guid teamId, CancellationToken cancellationToken) =>
        await _db.ProviderInstance
            .SingleOrDefaultAsync(i => i.Id == providerInstanceId && i.TeamId == teamId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Provider instance {providerInstanceId} not found");

    private Credential BuildPersonalCredential(ProviderInstance instance, Guid userId, Guid teamId, string accessToken)
    {
        var encrypted = _encryptor.Encrypt(_serializer.Serialize(new PatPayload { Token = accessToken }));

        return new Credential
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            ProviderInstanceId = instance.Id,
            OwnerUserId = userId,
            Ownership = CredentialOwnership.Personal,
            AuthType = AuthType.Pat,
            DisplayName = instance.Provider.ToString(),
            EncryptedPayload = encrypted,
            Status = CredentialStatus.Active
        };
    }

    /// <summary>Whoami via the generic probe capability. The transient credential isn't persisted yet,
    /// so an invalid token throws before any row is written.</summary>
    private async Task<CredentialProbeResult> ProbeOrThrowAsync(ProviderInstance instance, Credential credential, CancellationToken cancellationToken)
    {
        var probe = _registry.Require<ICredentialProbeCapability>(instance.Provider);
        var result = await probe.ProbeCredentialAsync(new ProviderContext(instance, credential), cancellationToken).ConfigureAwait(false);

        if (!result.IsValid)
            throw new InvalidOperationException($"Could not validate the token against {instance.Provider}: {result.Error ?? "unknown error"}");

        return result;
    }

    /// <summary>Soft-delete any existing live link for (user, instance) so the new one doesn't collide with
    /// the partial unique index — a re-link replaces in place.</summary>
    private async Task ReplaceExistingLinkAsync(Guid userId, Guid providerInstanceId, CancellationToken cancellationToken)
    {
        var existing = await _db.UserProviderIdentity
            .Include(i => i.Credential)
            .Where(i => i.UserId == userId && i.ProviderInstanceId == providerInstanceId && i.DeletedDate == null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        foreach (var old in existing)
        {
            old.DeletedDate = now;
            old.Credential.DeletedDate = now;
            old.Credential.Status = CredentialStatus.Revoked;
            old.Credential.EncryptedPayload = string.Empty;
        }
    }

    private static UserProviderIdentity BuildIdentity(Guid userId, Guid providerInstanceId, Guid credentialId, CredentialProbeResult profile) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        ProviderInstanceId = providerInstanceId,
        CredentialId = credentialId,
        ProviderUserId = profile.AuthenticatedUserExternalId ?? "",
        ProviderUsername = profile.AuthenticatedUserName ?? ""
    };

    private static UserProviderIdentitySummary ToSummary(UserProviderIdentity identity, ProviderKind provider, CredentialStatus credentialStatus) => new()
    {
        Id = identity.Id,
        ProviderInstanceId = identity.ProviderInstanceId,
        Provider = provider,
        ProviderUsername = identity.ProviderUsername,
        ProviderUserId = identity.ProviderUserId,
        AvatarUrl = identity.AvatarUrl,
        CredentialStatus = credentialStatus,
        CreatedDate = identity.CreatedDate
    };
}
