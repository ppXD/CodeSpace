using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Workspace;

/// <summary>
/// Resolves the workspace an agent run should be materialised with — the generic "where does the code
/// come from" seam. Given the run's task, it produces a <see cref="WorkspaceRequest"/> the
/// <see cref="IWorkspaceProvider"/> clones, or <c>null</c> when the run needs no workspace (an
/// analysis-only / no-repo run).
///
/// The source is open-ended on purpose: today the only source is the task's bound
/// <c>RepositoryId</c> (<see cref="RepositoryWorkspaceResolver"/>); a raw git URL, an upstream node's
/// produced branch, or a scratch workspace each land as additional sources behind this same interface —
/// the executor and the provider never change, because both sides speak only <see cref="WorkspaceRequest"/>.
/// </summary>
public interface IAgentWorkspaceResolver
{
    /// <summary>Resolve the workspace for this run, or <c>null</c> when none is needed. Throws <see cref="WorkspaceException"/> when a workspace is required but can't be resolved (repo missing, no clone URL), when the authored <c>WorkspaceSpec</c> has MORE THAN ONE repository (not yet executable — a later slice), or when it has none.</summary>
    Task<WorkspaceRequest?> ResolveAsync(AgentTask task, Guid teamId, CancellationToken cancellationToken);

    /// <summary>
    /// Resolve a workspace directly from a repository id (team-scoped) — the seam an integration step / a node holding
    /// only a <c>RepositoryId</c> (not a whole <see cref="AgentTask"/>) uses to obtain the clone URL + push token.
    /// <paramref name="ref"/> overrides the checked-out ref (null → the repository's default branch). Throws
    /// <see cref="WorkspaceException"/> when the repository can't be resolved (missing, no clone URL).
    /// </summary>
    Task<WorkspaceRequest?> ResolveByRepositoryIdAsync(Guid repositoryId, Guid teamId, CancellationToken cancellationToken, string? @ref = null);
}
