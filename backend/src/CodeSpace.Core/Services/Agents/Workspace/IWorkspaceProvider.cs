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
}
