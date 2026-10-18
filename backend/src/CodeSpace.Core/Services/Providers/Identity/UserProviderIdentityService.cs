using CodeSpace.Core.Authorization;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Teams;
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
    private readonly ICredentialSuccessionService _succession;
    private readonly ICredentialRevocationService _revocation;
    private readonly ITeamMemberService _members;

    public UserProviderIdentityService(CodeSpaceDbContext db, ICurrentUser currentUser, ICurrentTeam currentTeam, IPayloadEncryptor encryptor, ICredentialPayloadSerializer serializer, IProviderRegistry registry, ICredentialSuccessionService succession, ICredentialRevocationService revocation, ITeamMemberService members)
    {
        _db = db;
        _currentUser = currentUser;
        _currentTeam = currentTeam;
        _encryptor = encryptor;
        _serializer = serializer;
        _registry = registry;
        _succession = succession;
        _revocation = revocation;
        _members = members;
    }

    public async Task<UserProviderIdentitySummary> LinkByPatAsync(Guid providerInstanceId, string accessToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) throw new InvalidOperationException("Access token must not be empty.");

        var userId = _currentUser.Id!.Value;
        var teamId = _currentTeam.Id!.Value;

        var instance = await LoadInstanceAsync(providerInstanceId, teamId, cancellationToken).ConfigureAwait(false);

        var credential = BuildPersonalCredential(instance, userId, teamId, accessToken);
        var profile = await ProbeOrThrowAsync(instance, credential, cancellationToken).ConfigureAwait(false);

        await HoldCallerInTeamAsync(userId, teamId, cancellationToken).ConfigureAwait(false);

        credential.DisplayName = $"{instance.Provider} · {profile.AuthenticatedUserName}";

        // Persist the token's real granted scopes (the probe reads them live) so capability warnings
        // reflect this PAT — same as the OAuth flow stores token.GrantedScopes. Null when the provider
        // couldn't expose them; the capability check treats that as "unknown", never a false warning.
        credential.Scopes = profile.GrantedScopes?.ToList();

        // The probe just validated the token, and GitLab tells it when the token expires (every new one has an expiry, and
        // one that reaches it answers 401 to everything) — recorded so the list can say so ahead of time.
        credential.LastValidatedDate = DateTimeOffset.UtcNow;
        credential.ExpiresDate = profile.ExpiresAt;
        await ReplaceExistingLinkAsync(userId, providerInstanceId, cancellationToken).ConfigureAwait(false);

        await _db.Credential.AddAsync(credential, cancellationToken).ConfigureAwait(false);
        await _succession.CarryForwardAsync(credential, cancellationToken).ConfigureAwait(false);

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

        identity.DeletedDate = DateTimeOffset.UtcNow;

        await MoveBindingsToOnlyOtherConnectionAsync(identity.Credential, userId, cancellationToken).ConfigureAwait(false);

        // Retire the backing credential through the same cascade a Disconnect runs — an unlinked identity must leave
        // nothing usable at rest, and a repository bound through its token must say it needs a new one rather than
        // stay Active on a blanked payload. Still the caller's own token, from whichever team they ask.
        await _revocation.RetireAsync(identity.Credential, cancellationToken).ConfigureAwait(false);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// An unlink ends the person's connection exactly as a Disconnect does, so what a Disconnect that names nobody carries
    /// forward — everything bound through it, onto their one other live connection — an unlink carries forward too, before
    /// the cascade that would otherwise strand it. The same rule, the same refusals, one place:
    /// <see cref="ICredentialSuccessionService.FindOnlyOtherConnectionAsync"/>.
    /// </summary>
    private async Task MoveBindingsToOnlyOtherConnectionAsync(Credential ending, Guid userId, CancellationToken cancellationToken)
    {
        var successor = await _succession.FindOnlyOtherConnectionAsync(ending, userId, cancellationToken).ConfigureAwait(false);

        if (successor == null) return;

        await _succession.MoveBindingsAsync(ending, successor, cancellationToken).ConfigureAwait(false);
    }

    public async Task EnsureIdentityForCredentialAsync(ProviderInstance instance, Credential credential, Guid userId, CancellationToken cancellationToken)
    {
        if (!ConnectedBy(credential, userId)) return;

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

    /// <summary>
    /// The request was let in on the caller's standing, but the provider check runs between that and the insert, and the
    /// caller can leave — or be removed — during it. Asked again and, for a member, held until this link commits: a
    /// departure that already committed refuses the link, one that starts now waits and then retires the new token with
    /// the rest of theirs. A personal token in a team is lent by someone who stands in it — a member, or an instance Admin,
    /// who stands in every team (<see cref="TeamStanding"/>).
    /// </summary>
    private async Task HoldCallerInTeamAsync(Guid userId, Guid teamId, CancellationToken cancellationToken)
    {
        if (!await _members.HoldMembershipAsync(teamId, userId, cancellationToken).ConfigureAwait(false))
            throw new TenantAccessDeniedException(userId, teamId, "user is not a member of this team");
    }

    /// <summary>
    /// An identity says "this provider account speaks for this person", and only the person can say that — by connecting
    /// the account themselves. A token someone else connected in their name is whatever account THAT caller signed into
    /// at the provider; linking it would let them choose who the person's writes are attributed to.
    /// </summary>
    private static bool ConnectedBy(Credential credential, Guid userId) => credential.CreatedBy == userId;

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
