using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Workspace;

/// <summary>
/// Materialises an isolated working copy of a repository for an agent run, so the harness has real code
/// to read and edit. The seam that keeps workspace prep deployment-neutral — it mirrors
/// <c>ISandboxRunner</c>: v0 ships <c>LocalGitWorkspaceProvider</c> (a git clone on the worker
/// filesystem); a future K8s provider clones into the pod's volume behind the same contract, resolved by
/// the same <see cref="Kind"/>, taking the same <see cref="WorkspaceRequest"/> — no consumer change.
///
/// Narrow on purpose (Rule 7 / ISP): prepare a workspace, hand back a disposable handle. Richer needs
/// (sparse checkout, a warm cache, submodules) land as SIBLING capabilities, not new members here.
///
/// An implementation MUST be safe for concurrent invocations — each call yields its own fresh directory.
/// </summary>
public interface IWorkspaceProvider
{
    /// <summary>Stable tag, matching the sandbox runner it pairs with — "local" (v0), later "k8s". Matches the key the registry resolves by.</summary>
    string Kind { get; }

    /// <summary>
    /// Clone / check out per <paramref name="request"/> into a fresh isolated directory and return a
    /// handle to it. Throws <see cref="WorkspaceException"/> on failure (clone error, missing ref, git
    /// unavailable). The returned handle's <see cref="IWorkspaceHandle.DisposeAsync"/> removes the
    /// directory — the run owns it for its lifetime.
    /// </summary>
    Task<IWorkspaceHandle> PrepareAsync(WorkspaceRequest request, CancellationToken cancellationToken);
}

/// <summary>A prepared workspace. <see cref="DisposeAsync"/> removes the working directory (best-effort).</summary>
public interface IWorkspaceHandle : IAsyncDisposable
{
    /// <summary>Absolute path to the prepared working directory the agent runs in.</summary>
    string Directory { get; }

    /// <summary>
    /// Capture the agent's changes versus the cloned base — the unified diff + the changed-file paths
    /// (ground truth from git, covering staged, unstaged, and committed work). Returns an empty
    /// <see cref="WorkspaceChanges"/> when nothing changed. Throws <see cref="WorkspaceException"/> on a
    /// git failure. Call before <see cref="IAsyncDisposable.DisposeAsync"/> removes the directory.
    /// </summary>
    Task<WorkspaceChanges> CaptureChangesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A SIBLING capability (Rule 7 / ISP — NOT widening <see cref="IWorkspaceHandle"/>): push the agent's
/// changes from the prepared clone to a remote branch, so a downstream PR-open step has a branch that
/// pre-exists on the remote. Only the handles that can write back (the local-clone handle) implement it;
/// a feature-detect (<c>workspace is IWorkspacePushHandle</c>) keeps the base contract read-only.
/// </summary>
public interface IWorkspacePushHandle
{
    /// <summary>
    /// Commit everything in the clone and force-push it to <paramref name="branchName"/> on the origin
    /// remote, returning the pushed branch name on success. Returns null when the run made no changes
    /// (nothing to commit) or the clone carries no push credential (an anonymous clone) — neither is a
    /// failure. Throws <see cref="WorkspaceException"/> (with the token REDACTED) on a git failure.
    /// </summary>
    Task<string?> PushChangesAsync(string branchName, CancellationToken cancellationToken);
}
