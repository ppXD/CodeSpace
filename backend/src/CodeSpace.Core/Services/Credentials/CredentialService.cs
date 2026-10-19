using CodeSpace.Core.Authorization;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Providers.Modules;
using CodeSpace.Core.Services.Providers.Scopes;
using CodeSpace.Core.Services.Teams;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Credentials;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Credentials;

public sealed partial class CredentialService : ICredentialService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;
    private readonly IPayloadEncryptor _encryptor;
    private readonly ICredentialPayloadSerializer _serializer;
    private readonly IOAuthGrantRevoker _grants;
    private readonly IProviderRegistry _registry;
    private readonly IProviderModuleCatalog _modules;
    private readonly IScopeChecker _scopeChecker;
    private readonly ICredentialSuccessionService _succession;
    private readonly ICredentialRevocationService _revocation;
    private readonly ICredentialHealthService _health;
    private readonly ICredentialReauthorizer _reauthorizer;
    private readonly ICredentialRelinkService _relink;
    private readonly ITeamMemberService _members;
    private readonly TeamMembershipResolver _membership;
    private readonly ILogger<CredentialService> _logger;

    public CredentialService(CodeSpaceDbContext db, ICurrentTeam currentTeam, ICurrentUser currentUser, IPayloadEncryptor encryptor, ICredentialPayloadSerializer serializer, IOAuthGrantRevoker grants, IProviderRegistry registry, IProviderModuleCatalog modules, IScopeChecker scopeChecker, ICredentialSuccessionService succession, ICredentialRevocationService revocation, ICredentialHealthService health, ICredentialReauthorizer reauthorizer, ICredentialRelinkService relink, ITeamMemberService members, TeamMembershipResolver membership, ILogger<CredentialService> logger)
    {
        _db = db;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
        _encryptor = encryptor;
        _serializer = serializer;
        _grants = grants;
        _registry = registry;
        _modules = modules;
        _scopeChecker = scopeChecker;
        _succession = succession;
        _revocation = revocation;
        _health = health;
        _reauthorizer = reauthorizer;
        _relink = relink;
        _members = members;
        _membership = membership;
        _logger = logger;
    }

    public async Task<IReadOnlyList<CredentialSummary>> ListAsync(Guid? providerInstanceId, CancellationToken cancellationToken)
    {
        var teamId = _currentTeam.Id!.Value;
        var query = _db.Credential.AsNoTracking().Where(c => c.TeamId == teamId && c.DeletedDate == null);

        if (providerInstanceId.HasValue) query = query.Where(c => c.ProviderInstanceId == providerInstanceId.Value);

        // Left-join the owning user so the UI can show "alice's GitHub · alice" in the Add
        // Repository picker. Two credentials with the same DisplayName (e.g. after a rename)
        // would otherwise be ambiguous; OwnerUserName disambiguates without forcing the user
        // to think about display-name uniqueness.
        var summaries = await query
            .OrderByDescending(c => c.CreatedDate)
            .Select(c => new CredentialSummary
            {
                Id = c.Id,
                TeamId = c.TeamId,
                ProviderInstanceId = c.ProviderInstanceId,
                OwnerUserId = c.OwnerUserId,
                OwnerUserName = c.Owner != null ? c.Owner.Name : null,
                Ownership = c.Ownership,
                AuthType = c.AuthType,
                DisplayName = c.DisplayName,
                Status = c.Status,
                ExpiresDate = c.ExpiresDate,
                LastValidatedDate = c.LastValidatedDate,
                LastError = c.LastError,
                CreatedDate = c.CreatedDate
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var withHealth = await WithHealthAsync(summaries, teamId, cancellationToken).ConfigureAwait(false);

        return await WithCallerRepairAsync(withHealth, teamId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// What to read off each listed credential (<see cref="CredentialHealthDerivation"/>): the columns it is derived from are
    /// already on the summary, and whether its person has left is one more read for the team's list — the same rule the
    /// retirement sweep uses (<see cref="DepartedCredentials"/>), so the list never promises a retirement the sweep will not make.
    /// </summary>
    private async Task<IReadOnlyList<CredentialSummary>> WithHealthAsync(List<CredentialSummary> summaries, Guid teamId, CancellationToken cancellationToken)
    {
        var ids = summaries.Select(s => s.Id).ToList();
        var left = await DepartedCredentials.Of(_db).AsNoTracking().Where(c => c.TeamId == teamId && ids.Contains(c.Id)).Select(c => c.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var bound = await CountBoundRepositoriesAsync(teamId, ids, cancellationToken).ConfigureAwait(false);
        var hooks = await CountInServiceConnectionWebhooksAsync(ids, cancellationToken).ConfigureAwait(false);
        var identities = await CountLiveIdentitiesAsync(ids, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        return summaries.Select(s => s with
        {
            Health = CredentialHealthDerivation.Derive(s.Status, s.AuthType, s.ExpiresDate, left.Contains(s.Id), now),
            DaysToExpiry = CredentialHealthDerivation.DaysToExpiry(s.AuthType, s.ExpiresDate, now),
            BoundRepositoryCount = bound.GetValueOrDefault(s.Id),
            BoundConnectionWebhookCount = hooks.GetValueOrDefault(s.Id),
            ActAsIdentityCount = identities.GetValueOrDefault(s.Id),
            DependentCount = bound.GetValueOrDefault(s.Id) + hooks.GetValueOrDefault(s.Id) + identities.GetValueOrDefault(s.Id)
        }).ToList();
    }

    /// <summary>How many live repositories each listed credential holds — one grouped read for the whole list, so the number beside a credential (what reconnecting or disconnecting it puts at stake) costs the list no more than one query.</summary>
    private async Task<Dictionary<Guid, int>> CountBoundRepositoriesAsync(Guid teamId, List<Guid> credentialIds, CancellationToken cancellationToken) =>
        await _db.Repository.AsNoTracking()
            .Where(r => r.TeamId == teamId && r.DeletedDate == null && r.CredentialId != null && credentialIds.Contains(r.CredentialId.Value))
            .GroupBy(r => r.CredentialId!.Value)
            .Select(g => new { CredentialId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.CredentialId, g => g.Count, cancellationToken).ConfigureAwait(false);

    public async Task<CredentialUsage> GetUsageAsync(Guid credentialId, CancellationToken cancellationToken)
    {
        var credential = await LoadCredentialAsync(credentialId, cancellationToken).ConfigureAwait(false);

        // What a Disconnect asked right now would do — so what the confirm dialog warns about is what then happens: the same
        // resolution of the implied successor, by the same person.
        var successor = await ResolveSuccessorAsync(credential, requestedId: null, cancellationToken).ConfigureAwait(false);

        var bound = _db.Repository.AsNoTracking().Where(r => r.CredentialId == credentialId && r.DeletedDate == null);

        if (successor != null)
            return new CredentialUsage { CredentialId = credentialId, ActiveRepositoryCount = 0, WillMoveRepositoryCount = await bound.CountAsync(cancellationToken).ConfigureAwait(false), WillMoveToDisplayName = successor.DisplayName };

        // Active = not soft-deleted AND not already flipped to Error by a prior revoke.
        // Counting only Status=Active gives the right "you're about to break N working
        // repositories" number; previously-broken ones don't need to be in the warning.
        var activeCount = await bound.CountAsync(r => r.Status == RepositoryStatus.Active, cancellationToken).ConfigureAwait(false);

        return new CredentialUsage { CredentialId = credentialId, ActiveRepositoryCount = activeCount };
    }

    public async Task<CredentialCapabilitiesResponse> GetCapabilitiesAsync(Guid credentialId, CancellationToken cancellationToken)
    {
        var teamId = _currentTeam.Id!.Value;

        var credential = await _db.Credential
            .AsNoTracking()
            .Include(c => c.ProviderInstance)
            .SingleOrDefaultAsync(c => c.Id == credentialId && c.TeamId == teamId && c.DeletedDate == null, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Credential {credentialId} not found");

        var module = _modules.Get(credential.ProviderInstance.Provider);

        // No declared scope map → nothing to report. Surface no capabilities rather than blocking a
        // provider whose requirements haven't been declared yet.
        if (module == null) return BuildCapabilitiesResponse(credential, Array.Empty<CredentialCapabilityStatus>());

        var granted = credential.Scopes;

        // granted == null means we never captured this token's scopes (a PAT linked before scope-
        // capture existed, or a token type that can't expose them). null is UNKNOWN, not "zero
        // scopes" — surface every capability as available so we never show a FALSE "missing"
        // warning. Only a known scope list (incl. an explicitly empty one) is checked and can warn.
        if (granted == null)
            return BuildCapabilitiesResponse(credential, module.CapabilityScopeRequirements.Select(kvp => Available(kvp.Key)).ToList());

        var statuses = module.CapabilityScopeRequirements
            .Select(kvp => ToStatus(kvp.Key, _scopeChecker.Check(credential.ProviderInstance.Provider, kvp.Key, granted)))
            .ToList();

        return BuildCapabilitiesResponse(credential, statuses);
    }

    private static CredentialCapabilityStatus ToStatus(Type capability, ScopeCheckOutcome outcome) =>
        new() { Capability = capability.Name, IsAvailable = outcome.IsSatisfied, MissingScopes = outcome.MissingScopes };

    private static CredentialCapabilityStatus Available(Type capability) =>
        new() { Capability = capability.Name, IsAvailable = true };

    private static CredentialCapabilitiesResponse BuildCapabilitiesResponse(Credential credential, IReadOnlyList<CredentialCapabilityStatus> capabilities) =>
        new()
        {
            CredentialId = credential.Id,
            GrantedScopes = credential.Scopes ?? new List<string>(),
            Capabilities = capabilities
        };

    public async Task<Guid> AddAsync(AddCredentialInput input, CancellationToken cancellationToken)
    {
        var teamId = _currentTeam.Id!.Value;

        // Tenant invariant: the target provider instance MUST belong to the caller's team. The command
        // only gates team MEMBERSHIP (IRequireTeamMembership), not the instance — so without this a team
        // member could attach a credential to ANOTHER team's instance by passing a foreign
        // ProviderInstanceId, leaving a credential whose ProviderInstance navigation crosses the tenant
        // boundary (a confused-deputy seam: the platform would pair that instance's host + OAuth client
        // with this team's token). Conflate not-found with not-in-team — a 404 leaks nothing about
        // another team's instances.
        var instance = await LoadProviderInstanceInTeamAsync(input.ProviderInstanceId, teamId, cancellationToken).ConfigureAwait(false);

        var ownerUserId = OwnerOf(input);

        await EnsureOwnerWithinReachAsync(ownerUserId, cancellationToken).ConfigureAwait(false);

        var json = _serializer.Serialize(input.Payload);
        var encrypted = _encryptor.Encrypt(json);

        var credential = new Credential
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            ProviderInstanceId = input.ProviderInstanceId,
            OwnerUserId = ownerUserId,
            Ownership = input.Ownership,
            AuthType = input.Payload.Type,
            DisplayName = input.DisplayName,
            EncryptedPayload = encrypted,
            Status = CredentialStatus.Active
        };

        // Before it is tracked: the validation stamps the new row, and a provider that answers slowly holds nothing the unit of work has written.
        await _health.ValidateNewAsync(credential, instance, cancellationToken).ConfigureAwait(false);

        await _db.Credential.AddAsync(credential, cancellationToken).ConfigureAwait(false);
        await _succession.CarryForwardAsync(credential, cancellationToken).ConfigureAwait(false);

        return credential.Id;
    }

    public async Task<RevokeCredentialResult> RevokeAsync(Guid credentialId, Guid? moveBindingsToCredentialId, CancellationToken cancellationToken)
    {
        var credential = await LoadCredentialAsync(credentialId, cancellationToken).ConfigureAwait(false);

        // Before the provider is asked to revoke anything: a refused target must leave the credential exactly as it was.
        var successor = await ResolveSuccessorAsync(credential, moveBindingsToCredentialId, cancellationToken).ConfigureAwait(false);

        var (providerOk, providerError) = await EndAsync(credential, cancellationToken).ConfigureAwait(false);

        // Moved BEFORE the cascade, which then finds nothing left bound to the credential: the other order would turn
        // every repository Error and lift it again, and a Paused or Unreachable one would come back Active.
        var moved = await MoveBindingsAsync(credential, successor, cancellationToken).ConfigureAwait(false);
        var affectedRepoCount = await _revocation.CascadeAsync(credential.Id, cancellationToken).ConfigureAwait(false);

        return new RevokeCredentialResult
        {
            CredentialId = credential.Id,
            ProviderAcknowledged = providerOk,
            ProviderError = providerError,
            AffectedRepositoryCount = affectedRepoCount,
            MovedRepositoryCount = moved.RepositoryCount,
            MovedConnectionWebhookCount = moved.ConnectionWebhookCount,
            MovedToCredentialId = moved.Any ? successor!.Id : null
        };
    }

    /// <summary>
    /// Skip provider revoke + local-state update when already Revoked — the token is gone, nothing to revoke. The cascade
    /// that follows ALWAYS runs: it is the only path that heals an out-of-sync Repository row (Active pointing at a Revoked
    /// credential), which can happen if the cascade didn't exist when the credential was first revoked. Its
    /// Status != Error filter makes the call idempotent.
    /// </summary>
    private async Task<(bool Ok, string? Error)> EndAsync(Credential credential, CancellationToken cancellationToken)
    {
        if (credential.Status == CredentialStatus.Revoked) return (Ok: true, Error: null);

        var (providerOk, providerError) = await TryRevokeAtProviderAsync(credential, cancellationToken).ConfigureAwait(false);
        MarkRevokedLocally(credential, providerError);

        return (providerOk, providerError);
    }

    public async Task<RemoteRepositoryPage> ListAccessibleRepositoriesAsync(Guid credentialId, string? search, int page, int perPage, CancellationToken cancellationToken)
    {
        var credential = await _db.Credential
            .Include(c => c.ProviderInstance)
            .SingleOrDefaultAsync(c => c.Id == credentialId && c.DeletedDate == null, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Credential {credentialId} not found");

        var catalog = _registry.Require<IRepositoryCatalogCapability>(credential.ProviderInstance.Provider);
        var context = new ProviderContext(credential.ProviderInstance, credential);

        // Clamp untrusted inputs — Page 0/-1 would 422 on GitHub; PerPage > 100 silently
        // degrades. Same pattern as the PR-list clamps.
        var clampedPage = page < 1 ? 1 : page;
        var clampedPerPage = Math.Clamp(perPage, 1, ListAccessibleRepositoriesQuery.MaxPerPage);

        return await catalog.PagedListAccessibleAsync(context, search, clampedPage, clampedPerPage, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProviderInstance> LoadProviderInstanceInTeamAsync(Guid providerInstanceId, Guid teamId, CancellationToken cancellationToken) =>
        await _db.ProviderInstance.AsNoTracking().FirstOrDefaultAsync(p => p.Id == providerInstanceId && p.TeamId == teamId && p.DeletedDate == null, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Provider instance {providerInstanceId} not found.");

    /// <summary>
    /// A team-service credential belongs to the team, not a person — it never carries an owner, whatever it was handed. A
    /// personal one always does: the person named, or the caller when nobody is, as an OAuth connect already defaults it.
    /// An owner-less personal token was nobody's to retire when the person who added it left.
    /// </summary>
    private Guid? OwnerOf(AddCredentialInput input) => input.Ownership == CredentialOwnership.TeamService ? null : input.OwnerUserId ?? _currentUser.Id;

    /// <summary>
    /// Naming an owner is acting on that person: the team lists the token as theirs and its bindings follow their next
    /// connect. So the owner must have standing in this team — a member, or an instance Admin — and the caller must be them
    /// or outrank them; an unknown id and one from another team fail alike, before the foreign key could tell them apart.
    /// </summary>
    private async Task EnsureOwnerWithinReachAsync(Guid? ownerUserId, CancellationToken cancellationToken)
    {
        if (ownerUserId is not Guid ownerId) return;

        await _members.EnsureMayActOnAsync(ownerId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Credential> LoadCredentialAsync(Guid id, CancellationToken cancellationToken) =>
        await _db.Credential.Include(c => c.ProviderInstance).FirstOrDefaultAsync(c => c.Id == id && c.DeletedDate == null, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"credential {id} not found");

    private async Task<(bool Ok, string? Error)> TryRevokeAtProviderAsync(Credential credential, CancellationToken cancellationToken)
    {
        // Non-OAuth credentials (PAT, Project/Group AT, SSH key, Basic) have nothing to revoke
        // at the provider — only the user deleting it on their side counts.
        if (credential.AuthType != AuthType.OAuth) return (Ok: true, Error: null);

        if (string.IsNullOrWhiteSpace(credential.ProviderInstance.OauthClientId) || string.IsNullOrWhiteSpace(credential.ProviderInstance.OauthClientSecretEnc))
            return (Ok: false, Error: "provider instance missing OAuth client credentials");

        var payload = _grants.ReadPayload(credential);

        if (payload == null) return (Ok: false, Error: "credential payload is not a valid OAuth payload");

        try
        {
            await _grants.RevokeAsync(credential.ProviderInstance, _encryptor.Decrypt(credential.ProviderInstance.OauthClientSecretEnc!), payload, wholeGrant: true, cancellationToken).ConfigureAwait(false);
            return (Ok: true, Error: null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Provider revoke failed for credential {CredentialId}; clearing local payload anyway", credential.Id);
            return (Ok: false, Error: ex.Message);
        }
    }

    private static void MarkRevokedLocally(Credential credential, string? providerError)
    {
        // Clear encrypted payload — even if the provider call failed, removing token material
        // from our DB ensures nothing in CodeSpace can present it for new requests.
        credential.EncryptedPayload = string.Empty;
        credential.Status = CredentialStatus.Revoked;
        credential.LastError = providerError;
        credential.LastValidatedDate = null;
    }
}
