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
    /// null on the light collapsed-card path, which skips the extra reads needed to compute it), and continue (which
    /// <paramref name="completionParked"/> extends to the one Suspended shape the operator alone can move).
    /// </summary>
    /// <param name="completionParked">
    /// True when the completion authority refused this run's terminal and stamped it parked. Supplied by
    /// <c>RoomProjector</c> — the sole caller — which already reads the row, so the resolver stays a pure function of
    /// its inputs, the same bargain <see cref="RoomPublishState"/> makes. Defaults to false: the conservative reading
    /// for a caller that has not read the row, which offers the pre-park set of verbs rather than inventing an exit.
    /// </param>
    IReadOnlyList<RoomAction> ResolveTurnActions(Guid runId, WorkflowRunStatus status, RoomPublishState? publish = null, bool completionParked = false);
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

    /// <summary>How many repository branches the shared resolver returned. Zero keeps legacy callers on the boolean fallback.</summary>
    public int PublishedBranchCount { get; init; }

    /// <summary>Legacy-compatible direct link. Populated only when exactly one PR represents the whole published set.</summary>
    public string? OpenedPullRequestUrl { get; init; }

    /// <summary>Every distinct current PR URL in the published set. Multiple links stay behind the result action because <see cref="RoomAction"/> has only one URL slot.</summary>
    public IReadOnlyList<string> OpenedPullRequestUrls { get; init; } = Array.Empty<string>();

    /// <summary>True when at least one resolved published repository has no current PR manifest.</summary>
    public bool HasUnopenedPublishedBranch { get; init; }
}
