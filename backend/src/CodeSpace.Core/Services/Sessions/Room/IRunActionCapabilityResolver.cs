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
    /// <summary>The turn-level actions for a run in <paramref name="status"/> — open-trace (always), rerun-the-turn (terminal only), stop (non-terminal only).</summary>
    IReadOnlyList<RoomAction> ResolveTurnActions(Guid runId, WorkflowRunStatus status);
}
