using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Auth;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.Workspace;

/// <summary>
/// Resolves the agent workspace from the task's <see cref="WorkspaceSpec"/> (multi-repo PR1) — the canonical
/// workspace source, with the legacy bound <c>RepositoryId</c> as a back-compat shorthand a null spec derives
/// from. Loads each repository (team-scoped) with its provider instance + credential, then produces a
/// <see cref="WorkspaceRequest"/>: the HTTPS clone URL, the ref (the spec's per-repo ref, else the repo's
/// default branch), and a short-lived access token resolved through the same <see cref="IProviderAuthResolver"/>
/// the providers use (so OAuth refresh + every auth type are handled in one place). A run with no workspace
/// (no spec and no <c>RepositoryId</c>) resolves to <c>null</c>.
///
/// <para>Multi-repo is fully resolved here: <see cref="ResolveAsync"/> loops over EVERY repo in the spec and
/// produces one <see cref="WorkspaceRepositoryProvision"/> per repo (honouring each repo's per-repo ref). A
/// single-repo run is just the 1-element case — byte-identical to the legacy <c>RepositoryId</c> path. Each repo
/// is loaded TEAM-SCOPED and non-deleted; an unresolvable repo fails the whole provision loud rather than silently
/// dropping repos.</para>
/// </summary>
public sealed class RepositoryWorkspaceResolver : IAgentWorkspaceResolver, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IProviderAuthResolver _auth;

    public RepositoryWorkspaceResolver(CodeSpaceDbContext db, IProviderAuthResolver auth)
    {
        _db = db;
        _auth = auth;
    }

    public async Task<WorkspaceProvisionRequest?> ResolveAsync(AgentTask task, Guid teamId, CancellationToken cancellationToken)
    {
        var spec = CanonicalWorkspace(task);

        if (spec is null) return null;

        if (spec.Repositories.Count == 0)
            throw new WorkspaceException("Workspace spec has no repositories to resolve.");

        // Resolve EVERY repo in the spec into a concrete clone instruction (multi-repo PR2 — single-repo is the
        // 1-element case, byte-identical). Each repo's clone is the same per-repo WorkspaceRequest the legacy path
        // produced, honouring the spec's per-repo ref.
        var provisions = new List<WorkspaceRepositoryProvision>(spec.Repositories.Count);

        foreach (var repo in spec.Repositories)
        {
            var clone = await ResolveByRepositoryIdAsync(repo.RepositoryId, teamId, cancellationToken, repo.Ref, repo.RefSoftFallback, repo.PinnedSha).ConfigureAwait(false)
                ?? throw new WorkspaceException($"Repository {repo.RepositoryId} could not be resolved.");

            provisions.Add(new WorkspaceRepositoryProvision { Alias = repo.Alias, CloneRequest = clone, Path = repo.Path, Access = repo.Access, IsPrimary = repo.IsPrimary });
        }

        return new WorkspaceProvisionRequest { Repositories = provisions, PrimaryAlias = spec.PrimaryAlias, CwdMode = spec.CwdMode };
    }

    /// <summary>The canonical workspace for a task: the authored <see cref="AgentTask.Workspace"/>, else a single-repo workspace derived from the legacy <see cref="AgentTask.RepositoryId"/>, else null (a no-repo run).</summary>
    internal static WorkspaceSpec? CanonicalWorkspace(AgentTask task) =>
        task.Workspace ?? (task.RepositoryId is { } id ? WorkspaceSpec.FromRepository(id) : null);

    public async Task<WorkspaceRequest?> ResolveByRepositoryIdAsync(Guid repositoryId, Guid teamId, CancellationToken cancellationToken, string? @ref = null, bool softFallback = false, string? pinnedSha = null)
    {
        var repo = await LoadRepositoryAsync(repositoryId, teamId, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(repo.CloneUrlHttps))
            throw new WorkspaceException($"Repository {repositoryId} has no HTTPS clone URL to clone from.");

        var token = await ResolveTokenAsync(repo, cancellationToken).ConfigureAwait(false);

        var requestedRef = string.IsNullOrWhiteSpace(@ref) ? null : @ref;

        return new WorkspaceRequest
        {
            RepositoryUrl = repo.CloneUrlHttps,
            // The spec's per-repo ref when requested, else the repository's default branch (the legacy behaviour).
            Ref = requestedRef ?? repo.DefaultBranch,
            // Carry the default branch as the soft fallback ONLY for an EXPLICITLY-SOFT session-inherited ref
            // (softFallback) — a prior produced branch is transient (a merged PR auto-deletes it), so the provider
            // degrades to the default instead of failing the continuing run. Every other caller (an authored ref, the
            // acceptance grader's produced-branch ref, the integrate path) passes softFallback=false ⇒ DefaultRef null
            // ⇒ a HARD ref, used verbatim, fail loud if gone (never silently rewritten) ⇒ byte-identical to before.
            DefaultRef = softFallback && requestedRef is not null && !string.Equals(requestedRef, repo.DefaultBranch, StringComparison.Ordinal) ? repo.DefaultBranch : null,
            Token = token,
            TokenUsername = token is null ? null : TokenUsernameFor(repo.ProviderInstance.Provider),
            // S1: the immutable base pin — validated to a hex commit id (4-40 chars) so garbage can never reach the
            // git argv as a flag-shaped positional, then forced through a full clone + hard checkout by the provider.
            PinnedSha = ValidatePinnedSha(pinnedSha),
        };
    }

    /// <summary>Null for blank; a trimmed 4-40 lowercase-hex commit id otherwise — anything else fails LOUD (the pin's contract is an EXACT commit; a malformed pin is a caller bug, and rejecting it here also keeps flag-shaped garbage out of the git argv).</summary>
    internal static string? ValidatePinnedSha(string? pinnedSha)
    {
        if (string.IsNullOrWhiteSpace(pinnedSha)) return null;

        var trimmed = pinnedSha.Trim().ToLowerInvariant();

        if (trimmed.Length is < 4 or > 40 || !trimmed.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new WorkspaceException($"the pinned base commit '{pinnedSha.Trim()}' is not a valid git commit id (4-40 hex chars) — the pin's contract is an EXACT commit, so a malformed pin fails the provision loud");

        return trimmed;
    }

    private async Task<Repository> LoadRepositoryAsync(Guid repositoryId, Guid teamId, CancellationToken cancellationToken) =>
        await _db.Repository.AsNoTracking()
            .Include(r => r.ProviderInstance)
            .Include(r => r.Credential)
            // Team-scoped AND non-deleted (matches every other repo loader + the launch-time gate): a repo
            // soft-deleted between launch validation and clone-time must not still be cloned. This is the FAIL-CLOSED
            // contract for EVERY resolve, including a replay/rerun that re-resolves the repo live — a run whose repo
            // was deleted afterwards refuses to re-clone it (rather than reaching a stale remote via a still-valid
            // credential). Deliberate: an operator who removed a repo should not have old runs silently re-clone it.
            .SingleOrDefaultAsync(r => r.Id == repositoryId && r.TeamId == teamId && r.DeletedDate == null, cancellationToken).ConfigureAwait(false)
        ?? throw new WorkspaceException($"Repository {repositoryId} not found for this team.");

    /// <summary>Resolve a clone token through the provider auth layer. A repo with no bound credential clones anonymously (public repo).</summary>
    private async Task<string?> ResolveTokenAsync(Repository repo, CancellationToken cancellationToken)
    {
        if (repo.Credential is null) return null;

        var auth = await _auth.ResolveAsync(new ProviderContext(repo.ProviderInstance, repo.Credential), cancellationToken).ConfigureAwait(false);

        return auth.Token;
    }

    /// <summary>The HTTPS basic-auth username each provider expects paired with a token. Pure + internal so it's unit-pinned (Rule 8 spirit).</summary>
    internal static string TokenUsernameFor(ProviderKind provider) => provider switch
    {
        ProviderKind.GitHub => "x-access-token",
        ProviderKind.GitLab => "oauth2",
        _ => "x-access-token",
    };
}
