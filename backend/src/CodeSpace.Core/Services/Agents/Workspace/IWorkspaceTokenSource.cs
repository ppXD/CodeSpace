namespace CodeSpace.Core.Services.Agents.Workspace;

/// <summary>
/// Where a workspace gets the git credential it pushes with, at the moment it pushes. Narrow on purpose (Rule 7 / ISP): one
/// question, asked of a repository by id, so a host that keeps credentials some other way plugs in without touching the
/// provider, and the provider never learns how a token is refreshed.
///
/// <para>It exists because a credential outlives neither the run nor the process that read it: a GitLab OAuth access token
/// lives two hours and is revoked the moment ANY caller refreshes it, so the token a run was handed at provisioning can be
/// dead — expired, or rotated by a sibling run — by the time its last act, the push, runs. An implementation therefore
/// answers from the credential store as it stands now, never from a value it kept.</para>
/// </summary>
public interface IWorkspaceTokenSource
{
    /// <summary>
    /// The access token that authenticates <paramref name="repositoryId"/>'s remote right now, refreshed if its provider
    /// says it is due; null when the repository has no credential bound (an anonymous remote). Resolved for
    /// <paramref name="teamId"/> only, and fails closed like the provisioning resolve does: a repository that was deleted
    /// or moved to another team, or a credential that was disconnected, throws instead of answering from a stale copy.
    /// </summary>
    Task<string?> ResolveAsync(Guid repositoryId, Guid teamId, CancellationToken cancellationToken);
}
