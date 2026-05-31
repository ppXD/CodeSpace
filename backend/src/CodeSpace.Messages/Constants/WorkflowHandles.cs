namespace CodeSpace.Messages.Constants;

/// <summary>
/// Reserved node output-handle names the engine treats specially. A handle name is a wire
/// contract — it appears in saved <c>EdgeDefinition.SourceHandle</c> values and is mirrored by
/// the editor — so renaming one breaks existing workflows. Pinned by <c>WorkflowHandlesTests</c>.
/// </summary>
public static class WorkflowHandles
{
    /// <summary>
    /// Universal error output, available implicitly on every node. When a node fails (after any
    /// retries are exhausted), the engine routes the run down edges whose <c>SourceHandle</c> is
    /// this — turning the failure into data on a handler branch (the node's <c>error</c> output
    /// carries <c>{ "message": ... }</c>) — instead of failing the run. With NO edge on this
    /// handle, the failure fails the run: the default, pre-error-routing behaviour.
    /// </summary>
    public const string Error = "error";

    /// <summary>
    /// The catch output of a <c>flow.try</c> scope container. When ANY node in the try's body fails
    /// unhandled, the engine routes the run down edges whose <c>SourceHandle</c> is this (carrying the
    /// failure as the try node's <c>error</c> output) instead of down the default success output — the
    /// region-level try/catch boundary. A normal branch handle (live only when the body failed), so
    /// distinct from <see cref="Error"/> (which is live only on a node's OWN failure).
    /// </summary>
    public const string Catch = "catch";
}
