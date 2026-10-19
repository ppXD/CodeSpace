using Microsoft.EntityFrameworkCore;

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
    /// (<see cref="IRepositoryReadProbe"/>) — one that did not ends the check at once with a retryable refusal.
    ///
    /// <para>A target on another connection to the same host takes the repository to that connection, so it must also read the repository where CodeSpace
    /// has it: the same id at another path is another repository, or the same one renamed since CodeSpace last read it, and either way not something to
    /// move a binding across on a guess.</para>
    /// </summary>
    private async Task EnsureTargetReadsEveryRepositoryAsync(CancellationToken cancellationToken)
    {
        if (_ctx.Repositories.Count == 0) return;

        EnsureFewEnoughToVerify();

        var changesConnection = _ctx.To.ProviderInstanceId != _ctx.From.ProviderInstanceId;
        var unreadable = new List<string>();
        var elsewhere = new List<string>();

        foreach (var repository in _ctx.Repositories)
        {
            var read = await _probe.ReadAsync(_ctx.To, repository.ExternalId, _ctx.From.DisplayName, cancellationToken).ConfigureAwait(false);

            if (!read.Readable) unreadable.Add(repository.FullPath);
            else if (changesConnection && !read.IsAt(repository.FullPath)) elsewhere.Add($"{repository.FullPath} (the provider names it {read.FullPath})");
        }

        if (unreadable.Count + elsewhere.Count == 0) return;

        throw new InvalidOperationException(elsewhere.Count == 0 ? UnreadableMessage(unreadable) : RefusedMessage(unreadable, elsewhere));
    }

    private void EnsureFewEnoughToVerify()
    {
        if (_ctx.Repositories.Count <= MaxRepositoriesVerified) return;

        throw new InvalidOperationException($"{_ctx.Repositories.Count} repositories are bound through '{_ctx.From.DisplayName}', and one relink asks the target to read each of them first — at most {MaxRepositoriesVerified} in one action. Nothing was moved. Unbind the repositories that are no longer wanted, or re-link the rest one at a time.");
    }

    private string UnreadableMessage(List<string> unreadable)
    {
        var named = string.Join(", ", unreadable.Take(NamedUnreadableLimit));
        var more = unreadable.Count > NamedUnreadableLimit ? $" and {unreadable.Count - NamedUnreadableLimit} more" : string.Empty;

        return $"Credential '{_ctx.To.DisplayName}' cannot read {unreadable.Count} of the {_ctx.Repositories.Count} repositories bound through '{_ctx.From.DisplayName}': {named}{more}. Nothing was moved. Pick a credential that reaches them, or unbind those repositories first.";
    }

    /// <summary>A move onto another connection that some repositories cannot make: the ones the target cannot read, and the ones it reads somewhere else.</summary>
    private string RefusedMessage(List<string> unreadable, List<string> elsewhere)
    {
        var refused = unreadable.Count + elsewhere.Count;
        var parts = new List<string>();

        if (unreadable.Count > 0) parts.Add($"cannot read {string.Join(", ", unreadable.Take(NamedUnreadableLimit))}");
        if (elsewhere.Count > 0) parts.Add($"does not find {string.Join(", ", elsewhere.Take(NamedUnreadableLimit))} where CodeSpace has it");

        return $"Credential '{_ctx.To.DisplayName}' cannot take {refused} of the {_ctx.Repositories.Count} repositories bound through '{_ctx.From.DisplayName}' over to its connection: it {string.Join(", and ", parts)}. Nothing was moved. Pick a credential that reaches them, or unbind those repositories first.";
    }
}
