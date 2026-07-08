using CodeSpace.Messages.Dtos.Sessions.Room;

namespace CodeSpace.Core.Services.Sessions.Room;

/// <summary>
/// The Room's "Open PR" action (PR-6): opens a pull/merge request for a terminal run's published branch(es).
/// </summary>
public interface IRoomPullRequestService
{
    /// <summary>
    /// Opens (or, on a repeat call, reuses) a PR for every repository the run published a branch to. Throws
    /// <see cref="InvalidOperationException"/> when the run has no published branch to open one from, or when a
    /// single-repo run's owning repository can't be resolved — both 400s via the global exception filter. A
    /// multi-repo run's PER-REPO provider failures do NOT throw (the honesty invariant): they come back as a
    /// <see cref="RoomPullRequestDisposition.Failed"/> entry alongside any repo that DID open cleanly.
    /// </summary>
    Task<RoomPullRequestResult> OpenAsync(Guid workflowRunId, Guid teamId, Guid? actorUserId, CancellationToken cancellationToken);
}
