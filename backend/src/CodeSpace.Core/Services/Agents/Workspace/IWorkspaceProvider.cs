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
    /// Clone / check out every repository in <paramref name="request"/> into a fresh isolated workspace and
    /// return a handle to it. A single-repo provision clones flat at the workspace root (byte-identical to
    /// before); a multi-repo provision clones each repo into a <c>&lt;root&gt;/&lt;path&gt;</c> subdir. Throws
    /// <see cref="WorkspaceException"/> on failure (clone error, missing ref, git unavailable). The returned
    /// handle's <see cref="IWorkspaceHandle.DisposeAsync"/> removes the whole workspace — the run owns it.
    /// </summary>
    Task<IWorkspaceHandle> PrepareAsync(WorkspaceProvisionRequest request, CancellationToken cancellationToken);
}

/// <summary>A prepared workspace. <see cref="DisposeAsync"/> removes the working directory tree (best-effort).</summary>
public interface IWorkspaceHandle : IAsyncDisposable
{
    /// <summary>Absolute path to the prepared working directory the agent runs in (the cwd — the repo root for a single-repo workspace, the shared root for a multi-repo one, per the provision's cwd mode).</summary>
    string Directory { get; }

    /// <summary>The repositories materialised in this workspace, each with its on-disk directory + access — the multi-repo materialisation result. A single-repo workspace has exactly one entry whose directory equals <see cref="Directory"/>.</summary>
    IReadOnlyList<WorkspaceRepositoryHandle> Repositories { get; }

    /// <summary>The alias of the PRIMARY repo (the one the top-level capture/push targets, and the change set's anchor) — always one of <see cref="Repositories"/>'s aliases.</summary>
    string PrimaryAlias { get; }

    /// <summary>
    /// Capture the PRIMARY repo's changes versus the cloned base — the unified diff + the changed-file paths
    /// (ground truth from git, covering staged, unstaged, and committed work). Returns an empty
    /// <see cref="WorkspaceChanges"/> when nothing changed. Throws <see cref="WorkspaceException"/> on a
    /// git failure. Call before <see cref="IAsyncDisposable.DisposeAsync"/> removes the directory.
    /// </summary>
    Task<WorkspaceChanges> CaptureChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Multi-repo PR3: capture the changes of the repo named <paramref name="alias"/> (one of <see cref="Repositories"/>).
    /// Same semantics as the primary-only overload, scoped to one repo. Throws <see cref="WorkspaceException"/> for an
    /// unknown alias or a git failure.
    /// </summary>
    Task<WorkspaceChanges> CaptureChangesAsync(string alias, CancellationToken cancellationToken);
}

/// <summary>One repository materialised inside a workspace (multi-repo PR2): its alias, on-disk directory, and access. A pure handle-side noun co-located with the contract it belongs to.</summary>
public sealed record WorkspaceRepositoryHandle
{
    /// <summary>The short name the provision referred to this repo by (e.g. "web", "api").</summary>
    public required string Alias { get; init; }

    /// <summary>Absolute path to this repo's clone on disk (equals the workspace <see cref="IWorkspaceHandle.Directory"/> for a single-repo workspace; a subdir under it for a multi-repo one).</summary>
    public required string Directory { get; init; }

    /// <summary>Whether the agent may write this repo or only read it as context.</summary>
    public required Messages.Agents.WorkspaceAccess Access { get; init; }

    /// <summary>The ref this repo was cloned at — the PR base for the branch the agent produces, carried through so a downstream change-set PR-open needs no separately-authored target. Usually the repo's default branch; a tag when an author pinned one (a non-branch ref then makes that repo's open a per-repo Failed). Null when the clone carried no ref.</summary>
    public string? BaseBranch { get; init; }

    /// <summary>The cloned HEAD revision <see cref="IWorkspaceHandle.CaptureChangesAsync(CancellationToken)"/> diffs against — surfaced here (not just internal provider state) so a caller can persist it durably (the launch-time <c>SandboxHandle</c>) for a later path-only capture via <see cref="IWorkspacePathCapture"/>, when the live handle that prepared this clone no longer exists.</summary>
    public string? BaseSha { get; init; }
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
    /// Commit everything in the PRIMARY clone and force-push it to <paramref name="branchName"/> on the origin
    /// remote, returning the pushed branch name on success. Returns null when the run made no changes
    /// (nothing to commit) or the clone carries no push credential (an anonymous clone) — neither is a
    /// failure. Throws <see cref="WorkspaceException"/> (with the token REDACTED) on a git failure.
    /// </summary>
    Task<string?> PushChangesAsync(string branchName, CancellationToken cancellationToken);

    /// <summary>
    /// Multi-repo PR3: commit + push the repo named <paramref name="alias"/> (one of <c>Repositories</c>) to
    /// <paramref name="branchName"/> on ITS own origin. Same semantics as the primary-only overload, scoped to
    /// one repo — each repo pushes to its distinct remote, so the same branch name across the set is fine.
    /// Throws <see cref="WorkspaceException"/> for an unknown / read-only alias or a git failure.
    /// </summary>
    Task<string?> PushChangesAsync(string alias, string branchName, CancellationToken cancellationToken);

    /// <summary>
    /// P3b-2 (provider readback): the commit sha the LAST successful push to <paramref name="alias"/> (null = the
    /// primary repo) CONFIRMED on the remote — the local tip re-read from the remote via <c>ls-remote</c> after the
    /// push, so "arrival" is an observed remote fact, not a self-report. Null when the push didn't run, the
    /// readback could not confirm (transient ls-remote fault), or the remote tip didn't match (raced) — absence is
    /// honest, never a fabricated sha. Default null so read-only/fake handles are unaffected.
    /// </summary>
    string? LastPushedCommitSha(string? alias = null) => null;
}

/// <summary>
/// A SIBLING capability (Rule 7 / ISP — NOT widening <see cref="IWorkspaceProvider"/>): capture a diff from a bare
/// directory + recorded base SHA, with NO live <see cref="IWorkspaceHandle"/> in hand. This is the RE-ATTACH path's
/// only tool — the handle object that prepared the clone died with the original worker (a backend restart), but the
/// clone directory is deliberately left on disk for re-attach, and its base SHA survives on the run's durable
/// <c>SandboxHandle</c> (stamped at launch). Read-only + credential-free by design: re-attach never re-resolves a
/// push token, so this can only ever capture, never push (a re-attached run that needs a branch on the remote still
/// produces it on the original live path, same as before this capability existed). A feature-detect
/// (<c>provider is IWorkspacePathCapture</c>) keeps the base contract handle-scoped.
/// </summary>
public interface IWorkspacePathCapture
{
    /// <summary>Same git-diff semantics as <see cref="IWorkspaceHandle.CaptureChangesAsync(CancellationToken)"/>, scoped to a bare <paramref name="directory"/> against <paramref name="baseSha"/> instead of a live handle. Throws <see cref="WorkspaceException"/> when the directory is gone or the git command fails — the caller treats this exactly like any other best-effort capture (log + keep the result unchanged).</summary>
    Task<WorkspaceChanges> CaptureChangesFromPathAsync(string directory, string baseSha, CancellationToken cancellationToken);
}
