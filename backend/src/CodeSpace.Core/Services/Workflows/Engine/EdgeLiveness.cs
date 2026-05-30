using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Workflows.Engine;

/// <summary>
/// The single rule deciding whether an outgoing edge is "live" — whether the engine follows it
/// once its source node settles. It is the crux of BOTH branch routing and error routing, and a
/// subtle miss silently mis-routes runs, so it's a pure function with an exhaustive unit test.
/// Status alone (plus the handle + branch hints) determines liveness, so the in-walk progression
/// and the durable rehydrate reconstruct identical liveness from the ledger.
/// </summary>
public static class EdgeLiveness
{
    /// <summary>The implicit single output handle of a non-branch node; a null SourceHandle means this.</summary>
    public const string DefaultHandle = "out";

    /// <summary>
    /// True iff the edge should fire, given its source's terminal status, the edge's source handle,
    /// and (for a branch source) the chosen routing hints:
    /// <list type="bullet">
    ///   <item><b>Failure</b> → ONLY the reserved <c>error</c> handle is live (error routing); every
    ///         other handle is dead, so a failure with no error edge halts the run.</item>
    ///   <item><b>Success</b> → the <c>error</c> handle is NEVER live (it exists for failures only);
    ///         a normal/branch handle is live when there are no routing hints (single-output node)
    ///         or the hints include it.</item>
    ///   <item><b>Skipped / not-yet-terminal</b> → dead (skip propagates downstream).</item>
    /// </list>
    /// </summary>
    public static bool IsLive(NodeStatus sourceStatus, string? sourceHandle, IReadOnlySet<string>? routingHints)
    {
        var isErrorHandle = sourceHandle == WorkflowHandles.Error;

        if (sourceStatus == NodeStatus.Failure)
            return isErrorHandle;

        if (sourceStatus != NodeStatus.Success)
            return false;

        // Success: the error branch must NOT fire — it's only for failures. Normal/branch handles
        // follow the routing hints (null hints ⇒ the single default output is live).
        if (isErrorHandle)
            return false;

        return routingHints == null || routingHints.Contains(sourceHandle ?? DefaultHandle);
    }
}
