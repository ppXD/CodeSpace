using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Workspace;

/// <summary>
/// Resolves the tip COMMIT of a clone request's effective ref over the GIT transport itself (<c>git ls-remote</c>) —
/// the same transport, URL, and credential the eventual clone uses, so the resolved sha can never skew from what the
/// clone would materialize (a provider-API lookup could disagree with the git remote in tests and on mirrors; the
/// transport cannot). S1's launch-time immutable-base vector is built from these.
/// </summary>
public interface IRemoteTipResolver
{
    /// <summary>
    /// The tip commit sha of <paramref name="request"/>'s effective ref: <see cref="WorkspaceRequest.Ref"/> when set
    /// (falling back to <see cref="WorkspaceRequest.DefaultRef"/> under the request's own SOFT semantics when the ref
    /// is gone), else the remote's HEAD. An unreachable remote always throws <see cref="WorkspaceException"/> LOUD —
    /// the clone would fail the same way later, and the pin's contract is early, honest failure. A ref the remote
    /// does not have: throws when <paramref name="refRequired"/> (an operator/authored pin — its absence is an
    /// authoring error), returns null when not (an IMPLICIT recorded default branch — an empty just-created repo or
    /// a stale default-branch record launches UNPINNED, the pre-S1 behaviour, instead of failing an opportunistic
    /// pin). Null also for an empty remote's HEAD (nothing exists to pin).
    /// </summary>
    Task<string?> ResolveTipShaAsync(WorkspaceRequest request, bool refRequired, CancellationToken cancellationToken);
}
