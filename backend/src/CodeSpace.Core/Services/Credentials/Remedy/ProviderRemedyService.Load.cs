using System.Linq.Expressions;
using CodeSpace.Core.Authorization;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Failures;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Credentials.Remedy;

public sealed partial class ProviderRemedyService
{
    /// <summary>The most repositories one remedy names. A run launched against dozens still gets a card a person can read; the count it states is of what it names.</summary>
    private const int MaxRepositories = 10;

    /// <summary>The repositories the failure was about: the one it names, else the ones the run was launched against, else — when it names a credential and nothing else — whatever still leans on that credential.</summary>
    private async Task LoadRepositoriesAsync(CancellationToken cancellationToken)
    {
        var named = RepositoryIdsInPlay();
        var credentialId = _ctx.Failure.DetailGuid("credentialId");

        if (named.Count > 0) _ctx.Repositories = await ReadRepositoriesAsync(r => named.Contains(r.Id), cancellationToken).ConfigureAwait(false);
        else if (credentialId is { } leaning) _ctx.Repositories = await ReadRepositoriesAsync(r => r.CredentialId == leaning, cancellationToken).ConfigureAwait(false);
    }

    private IReadOnlyList<Guid> RepositoryIdsInPlay() => _ctx.Failure.RepositoryId is { } own ? new[] { own } : _ctx.ScopeRepositoryIds;

    private async Task<List<RepositoryRow>> ReadRepositoriesAsync(Expression<Func<Repository, bool>> predicate, CancellationToken cancellationToken) =>
        await _db.Repository.AsNoTracking()
            .Where(r => r.TeamId == _ctx.TeamId && r.DeletedDate == null)
            .Where(predicate)
            .OrderBy(r => r.FullPath)
            .Take(MaxRepositories)
            .Select(r => new RepositoryRow(r.Id, r.FullPath, r.Status, r.CredentialId, r.ProviderInstanceId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Every credential in play, retired or not: a retired one is exactly the one a remedy has to be able to name.</summary>
    private async Task LoadCredentialsAsync(CancellationToken cancellationToken)
    {
        var ids = _ctx.Repositories.Select(r => r.CredentialId).Append(_ctx.Failure.DetailGuid("credentialId")).OfType<Guid>().Distinct().ToList();

        if (ids.Count == 0) return;

        var rows = await _db.Credential.AsNoTracking()
            .Where(c => c.TeamId == _ctx.TeamId && ids.Contains(c.Id))
            .Select(c => new CredentialRow(c.Id, c.DisplayName, c.Ownership, c.OwnerUserId, c.AuthType, c.Status, c.ExpiresDate, c.LastValidatedDate, c.DeletedDate, c.ProviderInstanceId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        _ctx.Credentials = rows.ToDictionary(c => c.Id);
    }

    private void ChooseCredential()
    {
        var failing = _ctx.Failure.DetailGuid("credentialId");

        _ctx.Credential = failing is { } id && _ctx.Credentials.TryGetValue(id, out var named)
            ? named
            : _ctx.Repositories.Select(r => r.CredentialId).OfType<Guid>().Select(bound => _ctx.Credentials.GetValueOrDefault(bound)).FirstOrDefault(c => c is not null);
    }

    private async Task LoadOwnerNameAsync(CancellationToken cancellationToken)
    {
        if (_ctx.Credential?.OwnerUserId is not { } owner) return;

        _ctx.OwnerName = await _db.User.IgnoreQueryFilters().AsNoTracking().Where(u => u.Id == owner).Select(u => u.Name).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The credential's provider connection, else the repository's, else — for a redirect that named only the address it met — the team's connection that answers on it.</summary>
    private async Task LoadProviderInstanceAsync(CancellationToken cancellationToken)
    {
        var id = _ctx.Credential?.ProviderInstanceId ?? _ctx.Repositories.Select(r => (Guid?)r.ProviderInstanceId).FirstOrDefault();

        var rows = await _db.ProviderInstance.AsNoTracking()
            .Where(i => i.TeamId == _ctx.TeamId && i.DeletedDate == null && (id == null || i.Id == id))
            .Select(i => new InstanceRow(i.Id, i.DisplayName, i.Provider, i.BaseUrl, i.ApiUrl))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        _ctx.Instance = id is not null ? rows.FirstOrDefault() : rows.FirstOrDefault(i => AnswersOn(i, _ctx.Failure.Detail("requestedOrigin")));
    }

    private static bool AnswersOn(InstanceRow instance, string? origin) =>
        origin is { Length: > 0 } && Uri.TryCreate(instance.BaseUrl, UriKind.Absolute, out var url) && string.Equals($"{url.Scheme}://{url.IdnHost}{(url.IsDefaultPort ? "" : ":" + url.Port)}", origin, StringComparison.OrdinalIgnoreCase);

    /// <summary>The credentials the provider would still present — connected through it, not retired and not disconnected: <c>ProviderInstanceService</c> refuses to move the host of an instance while any exist, so a redirect's repair depends on the count.</summary>
    private async Task LoadConnectedCredentialsAsync(CancellationToken cancellationToken)
    {
        if (_ctx.Code != FailureCodes.ProviderBaseUrlRedirects || _ctx.Instance is not { } instance) return;

        _ctx.ConnectedCredentials = await _db.Credential.AsNoTracking()
            .CountAsync(c => c.TeamId == _ctx.TeamId && c.ProviderInstanceId == instance.Id && c.DeletedDate == null && c.Status != CredentialStatus.Revoked, cancellationToken).ConfigureAwait(false);
    }

    private async Task ResolveCallerAsync(CancellationToken cancellationToken)
    {
        _ctx.Role = await _membership.ResolveRoleAsync(_ctx.TeamId, cancellationToken).ConfigureAwait(false);
        _ctx.CallerId = _currentUser.Id;
    }

    /// <summary>
    /// Who the credential belongs to and the role they stand as, read the way the service that enforces a reconnect's reach reads it
    /// (<see cref="TeamStanding.RolesActedOnAsync"/>) — so a person who has left, or one an Admin cannot reach, is known here before the button is, not after the endpoint refuses.
    /// </summary>
    private async Task LoadPersonAsync(CancellationToken cancellationToken)
    {
        if (_ctx.Credential is not { } credential) return;

        var owners = credential.OwnerUserId is { } owner ? new[] { owner } : Array.Empty<Guid>();
        var standing = await TeamStanding.RolesActedOnAsync(_db, _ctx.TeamId, owners, cancellationToken).ConfigureAwait(false);

        _ctx.Person = CredentialPerson.Of(credential.Ownership, credential.OwnerUserId, _ctx.CallerId, standing);
    }

    private async Task<IReadOnlyList<(Guid UserId, string Name, TeamRole Role)>> ReadTeamAdministratorsAsync(CancellationToken cancellationToken)
    {
        var rows = await _db.TeamMembership.AsNoTracking()
            .Where(m => m.TeamId == _ctx.TeamId && (m.Role == TeamRole.Owner || m.Role == TeamRole.Admin))
            .Select(m => new { m.UserId, m.User.Name, m.Role })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // Owners before Admins, then by name — ordered here because a role is stored as its name, which sorts the wrong way round.
        return rows.OrderByDescending(m => TeamRoleRank.Of(m.Role)).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Select(m => (m.UserId, m.Name, m.Role)).ToList();
    }

    /// <summary>How many people a remedy names to ask. A team of forty Admins is not a list a person can use.</summary>
    private const int MaxPeople = 5;
}
