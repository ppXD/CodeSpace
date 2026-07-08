using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Dtos.Sessions.Room;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Sessions.Room;

/// <summary>
/// Computes the capability-aware <see cref="RoomAction"/>s for a turn from the SAME lifecycle predicates the write
/// path enforces (<c>WorkflowRunState</c>), so a rendered action's <c>enabled</c> can't diverge from whether the call
/// would actually succeed — a disabled action carries a reason instead of 422-ing on click. READ-ONLY.
/// (R1a covers the turn-level verbs; the per-node rerun gate — which needs the loaded definition closure — lands as a
/// follow-up that extends this seam.)
/// </summary>
public interface IRunActionCapabilityResolver : IScopedDependency
{
    /// <summary>
    /// The turn-level actions for a run in <paramref name="status"/> — open-trace (always), rerun-the-turn (terminal
    /// only), stop (non-terminal only), open-pull-request (PR-6, only when <paramref name="publish"/> is supplied —
    /// null on the light collapsed-card path, which skips the extra reads needed to compute it).
    /// </summary>
    IReadOnlyList<RoomAction> ResolveTurnActions(Guid runId, WorkflowRunStatus status, RoomPublishState? publish = null);
}

/// <summary>
/// The PR-6 gating signal for <see cref="RoomActionKind.OpenPullRequest"/> — computed by <c>RoomProjector</c> (which
/// already has the DB + ledger access) and handed in, so the resolver itself stays a pure function of its inputs
/// (no new DB dependency on a class whose whole contract is "decided purely by WorkflowRunState").
/// </summary>
public sealed record RoomPublishState
{
    /// <summary>True once the run's decision tape has a clean integrated branch (single- or multi-repo) — the SAME signal <c>SupervisorPublishGate</c>/<c>IRoomPullRequestService</c> read, never a second notion of "published".</summary>
    public required bool HasPublishedBranch { get; init; }

    /// <summary>An already-opened PR's link, when at least one published repository already has one recorded on its <c>PublishManifest</c> row. Null otherwise (button reads "Open PR", not "View PR").</summary>
    public string? OpenedPullRequestUrl { get; init; }
}
