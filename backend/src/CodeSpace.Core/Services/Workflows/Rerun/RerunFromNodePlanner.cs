using CodeSpace.Messages.Dtos.Workflows;

namespace CodeSpace.Core.Services.Workflows.Rerun;

/// <summary>
/// Pure derivation of a from-node rerun's RE-RUN set vs KEPT set (D7), over the run's frozen definition.
/// Registry-free + DB-free → unit-tested exhaustively across every graph shape (linear, diamond/parallel,
/// branch, error-edge, container, root, terminal, disconnected, cyclic-defensive).
///
/// <para>The RE-RUN set is the chosen node plus its transitive forward closure over the TOP-LEVEL edge set,
/// HANDLE-AGNOSTIC (every outgoing handle — plain, branch true/false/case, the reserved <c>error</c> handle).
/// Handle-agnostic is mandatory: edge-liveness is recomputed at run time from the re-run node's ACTUAL status,
/// so a re-run branch may take a DIFFERENT handle than the original — every forward-reachable node must re-run,
/// or a flipped branch would silently reuse a stale Success / fail to run a now-live target.</para>
///
/// <para>Generic across every shape because the engine's validator forbids an edge crossing a container
/// boundary (<c>DefinitionValidator.CheckNoEdgeCrossesContainerBoundary</c>): the set of top-level
/// (<c>ParentId == null</c>) nodes plus the edges wholly among them is a CLOSED, self-contained DAG — a
/// container's body connects to the rest only through the container node, never through a body node. So a
/// container in the closure re-runs its whole body atomically (the engine never settles body cells); the
/// traversal simply stops at the container id because body nodes aren't top-level. No special-casing.</para>
///
/// <para>v1 (D7-2) reruns from a TOP-LEVEL node only; a container-internal (map-branch / loop-iteration) start
/// is rejected — rerun the whole container instead (D7-4 opens body-internal granularity).</para>
/// </summary>
public static class RerunFromNodePlanner
{
    public static RerunPlan Plan(WorkflowDefinition definition, string fromNodeId)
    {
        var node = definition.Nodes.FirstOrDefault(n => n.Id == fromNodeId)
            ?? throw new RerunTargetNotFoundException($"Node '{fromNodeId}' does not exist in this run's definition; it cannot be a rerun target.");

        if (node.ParentId != null)
            throw new RerunTargetNotFoundException(
                $"Node '{fromNodeId}' is inside container '{node.ParentId}'. Re-run the whole container '{node.ParentId}' instead — re-running from a container-internal node isn't supported yet.");

        var topLevel = definition.Nodes.Where(n => n.ParentId == null).Select(n => n.Id).ToHashSet();

        // Adjacency over the closed top-level subgraph only (edges wholly among top-level nodes), handle-agnostic.
        var adjacency = definition.Edges
            .Where(e => topLevel.Contains(e.From) && topLevel.Contains(e.To))
            .GroupBy(e => e.From)
            .ToDictionary(g => g.Key, g => g.Select(e => e.To).ToList());

        var reRun = new HashSet<string>();
        var stack = new Stack<string>();
        stack.Push(fromNodeId);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!reRun.Add(id)) continue;   // visited-set makes a defensive cycle terminate
            foreach (var to in adjacency.GetValueOrDefault(id, new List<string>())) stack.Push(to);
        }

        var kept = topLevel.Where(id => !reRun.Contains(id)).ToHashSet();

        return new RerunPlan { ReRunNodeIds = reRun, KeptNodeIds = kept };
    }
}
