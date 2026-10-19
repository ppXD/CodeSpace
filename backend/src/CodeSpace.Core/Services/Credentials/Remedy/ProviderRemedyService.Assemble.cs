using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Redaction;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Credentials.Remedy;

public sealed partial class ProviderRemedyService
{
    /// <summary>What works now: a redirect is resolved by the provider's address having moved off the redirecting origin; anything else by every repository the failure names authenticating through a credential that works (or, naming none, the credential itself).</summary>
    private void DecideResolved()
    {
        var now = DateTimeOffset.UtcNow;

        _ctx.Resolved = _ctx.Code == Messages.Failures.FailureCodes.ProviderBaseUrlRedirects
            ? _ctx.Instance is { } instance && RemedyResolution.RedirectIsResolved(_ctx.Failure.Detail("requestedOrigin"), instance.BaseUrl)
            : RepositoriesWork(now);
    }

    private bool RepositoriesWork(DateTimeOffset now)
    {
        if (_ctx.Repositories.Count > 0) return _ctx.Repositories.All(repository => RepositoryWorks(repository, now));

        return _ctx.Credential is { } credential && RemedyResolution.FailingCredentialIsFixed(StateOf(credential), _ctx.Code, _ctx.At, now);
    }

    private bool RepositoryWorks(RepositoryRow repository, DateTimeOffset now)
    {
        var current = repository.CredentialId is { } id && _ctx.Credentials.TryGetValue(id, out var row) ? StateOf(row) : null;

        return RemedyResolution.RepositoryWorks(current, _ctx.Failure.DetailGuid("credentialId"), _ctx.Code, _ctx.At, now);
    }

    private static RemedyResolution.CredentialState StateOf(CredentialRow c) => new(c.Id, c.Status, c.AuthType, c.ExpiresDate, c.LastValidatedDate, Retired: c.DeletedDate != null);

    private void StateFacts()
    {
        var credential = _ctx.Credential;

        _ctx.Facts = new RemedyFacts
        {
            Provider = _ctx.Instance?.Provider.ToString(),
            CredentialName = credential?.DisplayName ?? _ctx.Failure.Detail("credentialName"),
            OwnerName = _ctx.OwnerName,
            CallerOwnsCredential = _ctx.Person is { IsCaller: true },
            OwnerRole = _ctx.Person?.Standing,
            CredentialOwnerLeft = _ctx.Person is { Standing: null },
            CredentialRetired = credential?.DeletedDate != null,
            CredentialIsStaticToken = credential is { } c && CredentialHealthDerivation.IsStaticToken(c.AuthType),
            CredentialIsTeamService = credential is not null && _ctx.Person is null,
            Repositories = RepositoryPhrase(),
            RepositoryCount = _ctx.Repositories.Count,
            RedirectsTo = _ctx.Failure.Detail("finalOrigin"),
            RequestedOrigin = _ctx.Failure.Detail("requestedOrigin"),
            RedirectFix = RedirectFixFor(),
            ConnectedCredentials = _ctx.ConnectedCredentials,
            InstanceName = _ctx.Instance?.DisplayName,
            ProviderStatus = _ctx.Failure.Detail("providerStatus"),
            OAuthError = _ctx.Failure.Detail("oauthError"),
            Lookup = _ctx.Failure.Detail(Messages.Constants.StandingLookupCauses.DetailKey),
            Resolved = _ctx.Resolved,
            FixIsObservable = RemedyResolution.FixIsObservable(_ctx.Code) || _ctx.Code == Messages.Constants.WorkflowRunRequestRejectionReasons.CredentialRejected
        };
    }

    /// <summary>For a redirect on a known provider connection, the repair its server would accept; nothing otherwise.</summary>
    private RedirectRepair? RedirectFixFor() =>
        _ctx.Code == Messages.Failures.FailureCodes.ProviderBaseUrlRedirects && _ctx.Instance is { } instance
            ? RemedyResolution.RepairRedirect(_ctx.Failure.Detail("requestedOrigin"), _ctx.Failure.Detail("finalOrigin"), instance.BaseUrl, instance.ApiUrl, _ctx.ConnectedCredentials)
            : null;

    private string? RepositoryPhrase() => _ctx.Repositories.Count switch
    {
        0 => null,
        1 => _ctx.Repositories[0].FullPath,
        2 => $"{_ctx.Repositories[0].FullPath} and {_ctx.Repositories[1].FullPath}",
        var many => $"{many} repositories"
    };

    private void ChooseActions()
    {
        var caller = CallerStanding.Of(_ctx.Role, _ctx.Person);
        var subjects = new RemedyActions.ActionSubjects(HasCredential: _ctx.Credential is not null, HasRepositories: _ctx.Repositories.Count > 0, HasProviderInstance: _ctx.Instance is not null, OffersRerun: _ctx.OffersRerun);

        _ctx.Actions = RemedyActions.Build(_ctx.Code, _ctx.Facts, caller, subjects);
    }

    private async Task<ProviderRemedy> AssembleAsync(CancellationToken cancellationToken) => new()
    {
        Code = _ctx.Code,
        Title = RemedyCopy.Title(_ctx.Code, _ctx.Facts),
        Cause = RemedyCopy.Cause(_ctx.Code, _ctx.Facts),
        Resolved = _ctx.Resolved,
        Credential = _ctx.Credential is { } credential ? ToCredential(credential) : null,
        Repositories = _ctx.Repositories.Select(repository => ToRepository(repository)).ToList(),
        ProviderInstance = _ctx.Instance is { } instance ? new RemedyProviderInstance { Id = instance.Id, DisplayName = instance.DisplayName, Provider = instance.Provider, BaseUrl = CredentialUrl.Masked(instance.BaseUrl)! } : null,
        RedirectsTo = _ctx.Failure.Detail("finalOrigin"),
        Actions = _ctx.Actions,
        AskPeople = await WhoToAskAsync(cancellationToken).ConfigureAwait(false)
    };

    private RemedyCredential ToCredential(CredentialRow c) => new()
    {
        Id = c.Id,
        DisplayName = c.DisplayName,
        Ownership = c.Ownership,
        OwnerUserId = c.OwnerUserId,
        OwnerName = _ctx.OwnerName,
        AuthType = c.AuthType,
        Status = c.Status,
        Health = CredentialHealthDerivation.Derive(c.Status, c.AuthType, c.ExpiresDate, ownerLeft: _ctx.Person is { Standing: null }, DateTimeOffset.UtcNow),
        Retired = c.DeletedDate != null,
        ProviderInstanceId = c.ProviderInstanceId
    };

    private RemedyRepository ToRepository(RepositoryRow r) => new() { Id = r.Id, FullPath = r.FullPath, Status = r.Status, CredentialId = r.CredentialId, CredentialWorks = RepositoryWorks(r, DateTimeOffset.UtcNow) };

    /// <summary>
    /// Who to ask when something is not the caller's to do: the credential's own person for a repair only they can make, then the team's Owners and
    /// Admins who could do what the caller cannot. Empty when every action is the caller's — a list of people to ask beside buttons that work would only be noise.
    /// </summary>
    private async Task<IReadOnlyList<RemedyPerson>> WhoToAskAsync(CancellationToken cancellationToken)
    {
        var refused = _ctx.Actions.Where(a => !a.Enabled && a.Kind is not RemedyActionKind.Rerun).ToList();

        if (refused.Count == 0) return Array.Empty<RemedyPerson>();

        var people = new List<RemedyPerson>();

        // Putting the credential right in place — a reconnect or a token replace — is its own person's to make, so they are named first, as long as they still stand in the team.
        var repairIsTheirs = refused.Any(a => a.Kind is RemedyActionKind.Reconnect or RemedyActionKind.ReplaceToken);

        if (repairIsTheirs && _ctx.Person is { Standing: not null } && _ctx.Credential is { OwnerUserId: { } owner } && owner != _ctx.CallerId && _ctx.OwnerName is { } ownerName)
            people.Add(new RemedyPerson { UserId = owner, Name = ownerName, Role = "Owner of the credential" });

        var administrators = await ReadTeamAdministratorsAsync(cancellationToken).ConfigureAwait(false);

        people.AddRange(administrators.Where(a => a.UserId != _ctx.CallerId && people.All(p => p.UserId != a.UserId) && CouldDoWhatIsRefused(a.Role, refused)).Select(a => new RemedyPerson { UserId = a.UserId, Name = a.Name, Role = a.Role.ToString() }));

        return people.Take(MaxPeople).ToList();
    }

    /// <summary>
    /// Whether an Owner or Admin could do at least one of what the caller cannot: they hold every permission, but a reconnect also needs the reach over
    /// the credential's person — an Admin cannot sign in as another Admin — and nobody repairs what has no person in the team to repair it for.
    /// </summary>
    private bool CouldDoWhatIsRefused(TeamRole administrator, IReadOnlyList<RemedyAction> refused) => refused.Any(action => action.Kind switch
    {
        RemedyActionKind.Reconnect => _ctx.Person is { Standing: { } person } && TeamRoleRank.Reaches(administrator, person, isOwn: false),
        RemedyActionKind.ReplaceToken => _ctx.Person is null or { Standing: not null },
        _ => true
    });
}
