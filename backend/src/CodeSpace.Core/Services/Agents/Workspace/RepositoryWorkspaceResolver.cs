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
/// <para>Multi-repo PR1 scope: the data model + canonicalization land here; a workspace with MORE THAN ONE repo
/// is not yet executable (the multi-dir handle + clone arrives a slice later) and is REFUSED with a clear error,
/// so a single-repo run is byte-identical and a premature multi-repo authoring fails loud rather than silently
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

    public async Task<WorkspaceRequest?> ResolveAsync(AgentTask task, Guid teamId, CancellationToken cancellationToken)
    {
        var spec = CanonicalWorkspace(task);

        if (spec is null) return null;

        if (spec.Repositories.Count > 1)
            throw new WorkspaceException("Multi-repo workspaces are not yet executable — this run authored more than one repository. Single-repo runs are unaffected.");

        var primary = spec.Primary
            ?? throw new WorkspaceException("Workspace spec has no repositories to resolve.");

        // async (not a bare Task return) so the guard throws above surface as a faulted Task AT THE AWAIT, matching
        // the interface's async contract — a future caller that captures the Task (Task.WhenAll, deferred await)
        // sees the exception where it awaits, not synchronously at the call site.
        return await ResolveByRepositoryIdAsync(primary.RepositoryId, teamId, cancellationToken, primary.Ref).ConfigureAwait(false);
    }

    /// <summary>The canonical workspace for a task: the authored <see cref="AgentTask.Workspace"/>, else a single-repo workspace derived from the legacy <see cref="AgentTask.RepositoryId"/>, else null (a no-repo run).</summary>
    internal static WorkspaceSpec? CanonicalWorkspace(AgentTask task) =>
        task.Workspace ?? (task.RepositoryId is { } id ? WorkspaceSpec.FromRepository(id) : null);

    public async Task<WorkspaceRequest?> ResolveByRepositoryIdAsync(Guid repositoryId, Guid teamId, CancellationToken cancellationToken, string? @ref = null)
    {
        var repo = await LoadRepositoryAsync(repositoryId, teamId, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(repo.CloneUrlHttps))
            throw new WorkspaceException($"Repository {repositoryId} has no HTTPS clone URL to clone from.");

        var token = await ResolveTokenAsync(repo, cancellationToken).ConfigureAwait(false);

        return new WorkspaceRequest
        {
            RepositoryUrl = repo.CloneUrlHttps,
            // The spec's per-repo ref when authored, else the repository's default branch (the legacy behaviour).
            Ref = string.IsNullOrWhiteSpace(@ref) ? repo.DefaultBranch : @ref,
            Token = token,
            TokenUsername = token is null ? null : TokenUsernameFor(repo.ProviderInstance.Provider),
        };
    }

    private async Task<Repository> LoadRepositoryAsync(Guid repositoryId, Guid teamId, CancellationToken cancellationToken) =>
        await _db.Repository.AsNoTracking()
            .Include(r => r.ProviderInstance)
            .Include(r => r.Credential)
            .SingleOrDefaultAsync(r => r.Id == repositoryId && r.TeamId == teamId, cancellationToken).ConfigureAwait(false)
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
