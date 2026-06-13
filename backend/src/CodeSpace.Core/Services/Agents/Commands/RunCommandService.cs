using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Auth;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.Commands;

public sealed class RunCommandService : IRunCommandService, IScopedDependency
{
    /// <summary>Default runner/workspace backend when the request names none — matches <c>AgentRunExecutor.DefaultRunnerKind</c>.</summary>
    private const string DefaultRunnerKind = "local";

    private readonly CodeSpaceDbContext _db;
    private readonly IProviderAuthResolver _auth;
    private readonly ISandboxRunnerRegistry _runners;
    private readonly IWorkspaceProviderRegistry _workspaces;

    public RunCommandService(CodeSpaceDbContext db, IProviderAuthResolver auth, ISandboxRunnerRegistry runners, IWorkspaceProviderRegistry workspaces)
    {
        _db = db;
        _auth = auth;
        _runners = runners;
        _workspaces = workspaces;
    }

    public async Task<SandboxResult> RunAsync(RunCommandRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Command))
            throw new InvalidOperationException("A command is required.");

        var runnerKind = string.IsNullOrWhiteSpace(request.RunnerKind) ? DefaultRunnerKind : request.RunnerKind;
        var runner = _runners.Resolve(runnerKind);

        // Repo-scoped → clone into a fresh per-run workspace the command runs in; ephemeral → no checkout.
        // The same runnerKind selects the matching workspace provider, so a future docker/k8s pair composes here.
        var workspace = request.RepositoryId is { } repositoryId
            ? await _workspaces.Resolve(runnerKind).PrepareAsync(await BuildWorkspaceRequestAsync(repositoryId, request.Ref, request.TeamId, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false)
            : null;

        try
        {
            var spec = new SandboxSpec
            {
                Command = request.Command,
                Args = request.Args,
                WorkingDirectory = workspace?.Directory,
                Environment = request.Environment,
                TimeoutSeconds = request.TimeoutSeconds,
                AllowNetwork = request.AllowNetwork,
                MaxProcesses = request.MaxProcesses,
                MaxFileSizeMb = request.MaxFileSizeMb,
            };

            return await runner.RunAsync(spec, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (workspace != null) await workspace.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Repo → clone request: load the repository (by id, like the git.* node services), resolve a short-lived
    /// token through the same provider auth layer the resolver uses, and reuse its provider→username table so
    /// there's one source of truth. A repo with no bound credential clones anonymously (public / local repo).
    /// </summary>
    private async Task<WorkspaceRequest> BuildWorkspaceRequestAsync(Guid repositoryId, string? gitRef, Guid? teamId, CancellationToken cancellationToken)
    {
        // Fail-closed tenant scope: the repo is resolved ONLY within the run's team, so a model-supplied /
        // untrusted repositoryId can never clone another tenant's repo. No team context with a repo requested is
        // refused outright. A repo in another team falls out of the filter → the same non-leaking "not found".
        if (teamId is not { } team)
            throw new WorkspaceException("Cannot clone a repository without a team context for the run.");

        var repo = await _db.Repository
            .Include(r => r.ProviderInstance)
            .Include(r => r.Credential)
            .SingleOrDefaultAsync(r => r.Id == repositoryId && r.TeamId == team && r.DeletedDate == null, cancellationToken).ConfigureAwait(false)
            ?? throw new WorkspaceException($"Repository {repositoryId} not found.");

        if (string.IsNullOrWhiteSpace(repo.CloneUrlHttps))
            throw new WorkspaceException($"Repository {repositoryId} has no HTTPS clone URL to clone from.");

        var token = await ResolveTokenAsync(repo, cancellationToken).ConfigureAwait(false);

        return new WorkspaceRequest
        {
            RepositoryUrl = repo.CloneUrlHttps,
            Ref = string.IsNullOrWhiteSpace(gitRef) ? repo.DefaultBranch : gitRef,
            Token = token,
            TokenUsername = token is null ? null : RepositoryWorkspaceResolver.TokenUsernameFor(repo.ProviderInstance.Provider),
        };
    }

    private async Task<string?> ResolveTokenAsync(Repository repo, CancellationToken cancellationToken)
    {
        if (repo.Credential is null) return null;

        var auth = await _auth.ResolveAsync(new ProviderContext(repo.ProviderInstance, repo.Credential), cancellationToken).ConfigureAwait(false);

        return auth.Token;
    }
}
