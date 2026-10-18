using CodeSpace.Core.Services.OAuth;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Messages.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Credentials;

public sealed partial class CredentialRelinkService
{
    /// <summary>
    /// Most repositories one relink asks the target to read. The check is one provider call per repository, in sequence — the
    /// target may refresh its token on the first, and a refresh must not race itself — inside the request's database
    /// transaction; past this many the request would outlast the provider's patience and the client's. More than this are
    /// re-linked a few at a time (unbind what is no longer wanted, or re-link one repository at a time).
    /// </summary>
    public const int MaxRepositoriesVerified = 100;

    /// <summary>Most repositories named in a refusal — enough to act on, short enough to read.</summary>
    private const int NamedUnreadableLimit = 5;

    private async Task LoadBoundRepositoriesAsync(CancellationToken cancellationToken)
    {
        var from = _ctx.From;

        _ctx.Repositories = await _db.Repository.AsNoTracking()
            .Where(r => r.CredentialId == from.Id && r.TeamId == from.TeamId && r.ProviderInstanceId == from.ProviderInstanceId && r.DeletedDate == null)
            .OrderBy(r => r.FullPath)
            .Select(r => new BoundRepository(r.ExternalId, r.FullPath))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Ask the provider, as the target, for each repository it is about to take — the lookup a bind makes. A target that
    /// cannot read one would turn that repository's loud failure into a quiet one (runs 404 instead of naming a
    /// disconnected credential), so the whole move is refused and the unreadable ones are named: unbind them, or pick a
    /// credential that reaches them. Sequential and read-only, and bounded (<see cref="MaxRepositoriesVerified"/>); with
    /// nothing bound there is nothing to ask. Only a provider that ANSWERED says anything about the target
    /// (<see cref="TargetIsRefused"/>) — one that did not ends the check at once with a retryable refusal.
    /// </summary>
    private async Task EnsureTargetReadsEveryRepositoryAsync(CancellationToken cancellationToken)
    {
        if (_ctx.Repositories.Count == 0) return;

        EnsureFewEnoughToVerify();

        var catalog = _registry.Require<IRepositoryCatalogCapability>(_ctx.To.ProviderInstance.Provider);
        var context = new ProviderContext(_ctx.To.ProviderInstance, _ctx.To);
        var unreadable = new List<string>();

        foreach (var repository in _ctx.Repositories)
        {
            if (!await CanReadAsync(catalog, context, repository, cancellationToken).ConfigureAwait(false)) unreadable.Add(repository.FullPath);
        }

        if (unreadable.Count == 0) return;

        throw new InvalidOperationException(UnreadableMessage(unreadable));
    }

    private void EnsureFewEnoughToVerify()
    {
        if (_ctx.Repositories.Count <= MaxRepositoriesVerified) return;

        throw new InvalidOperationException($"{_ctx.Repositories.Count} repositories are bound through '{_ctx.From.DisplayName}', and one relink asks the target to read each of them first — at most {MaxRepositoriesVerified} in one action. Nothing was moved. Unbind the repositories that are no longer wanted, or re-link the rest one at a time.");
    }

    private async Task<bool> CanReadAsync(IRepositoryCatalogCapability catalog, ProviderContext context, BoundRepository repository, CancellationToken cancellationToken)
    {
        try
        {
            await catalog.GetByExternalIdAsync(context, repository.ExternalId, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (TargetIsRefused(ex))
        {
            _logger.LogWarning(ex, "Credential {CredentialId} could not read repository {RepositoryPath}; the relink from {FromCredentialId} is refused", context.Credential.Id, repository.FullPath, _ctx.From.Id);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not check whether credential {CredentialId} reads repository {RepositoryPath}, because the provider did not answer; the relink from {FromCredentialId} is refused, to be tried again", context.Credential.Id, repository.FullPath, _ctx.From.Id);

            throw new RelinkVerificationUnavailableException(_ctx.To.DisplayName, _ctx.From.DisplayName, ex);
        }
    }

    /// <summary>
    /// The provider answered, and the answer is about the target: this token is not let in (401, 403 or an insufficient
    /// scope), cannot see that project (404), or — for an OAuth connection — the grant itself was refused when its token was
    /// refreshed (the provider's answer, or the credential already marked as needing a reconnect for it). Anything else (a 5xx, a 429, a dropped connection, a local rate limit) says nothing about the target.
    /// </summary>
    private static bool TargetIsRefused(Exception ex) => ex is ProviderInsufficientScopeException or OAuthExchangeException or CredentialNeedsReconnectException or ProviderApiException { StatusCode: 401 or 403 or 404 };

    private string UnreadableMessage(List<string> unreadable)
    {
        var named = string.Join(", ", unreadable.Take(NamedUnreadableLimit));
        var more = unreadable.Count > NamedUnreadableLimit ? $" and {unreadable.Count - NamedUnreadableLimit} more" : string.Empty;

        return $"Credential '{_ctx.To.DisplayName}' cannot read {unreadable.Count} of the {_ctx.Repositories.Count} repositories bound through '{_ctx.From.DisplayName}': {named}{more}. Nothing was moved. Pick a credential that reaches them, or unbind those repositories first.";
    }
}
