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
}
