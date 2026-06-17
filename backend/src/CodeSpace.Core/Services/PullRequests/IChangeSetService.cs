using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.PullRequests;

/// <summary>
/// Converges a multi-repo Change Set into reviewable output: opens ONE pull/merge request per repository in the set,
/// each team-scoped through <see cref="IPullRequestService.OpenPullRequestAsync"/> (full repo + credential + capability
/// preflight per repo). The reusable seam BOTH the <c>git.open_change_set</c> node and (later) the supervisor merge lane
/// call — so the per-repo loop + failure-isolation policy lives in one place (Rule 16: thin caller → service).
///
/// <para>Failure-ISOLATED (the honesty invariant): one repo's provider rejection (403/422/scope) is recorded as a Failed
/// disposition and NEVER sinks the rest of the set; a repo with no source branch is a clean Skip, not a failure.</para>
/// </summary>
public interface IChangeSetService
{
    Task<ChangeSetResult> OpenPullRequestsAsync(Guid teamId, ChangeSetPullRequestSpec spec, Guid? actorUserId, CancellationToken cancellationToken);
}
