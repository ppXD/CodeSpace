namespace CodeSpace.Messages.Constants;

/// <summary>
/// The <c>workflow_run_node.iteration_key</c> discriminator. A node that runs once at the graph's top level
/// carries <see cref="TopLevel"/> (the empty string); a node inside a container body (loop / map iteration)
/// carries a non-empty per-iteration key like <c>"&lt;containerId&gt;#&lt;i&gt;"</c>.
///
/// <para>This was historically a bare <c>""</c> literal re-spelled in the engine, the from-node rerun seeder,
/// and the rerun reusability resolver. Centralising it removes the silent-drift hazard: if the sentinel ever
/// changes, every reader moves together and the pin test below fails loudly rather than the rerun queries
/// silently matching zero cells.</para>
/// </summary>
public static class WorkflowIterationKeys
{
    /// <summary>A top-level (non-iteration) node cell. The engine reads/writes top-level ledger rows under this key.</summary>
    public const string TopLevel = "";
}
