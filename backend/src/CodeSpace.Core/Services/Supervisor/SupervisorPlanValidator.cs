using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The PURE Tier-0 structural validator for a model-authored <c>plan</c> (loopability — no DB, no state, the sibling of
/// <see cref="SupervisorBounds"/> and <see cref="SupervisorDependencyGate"/>). The model authors a plan's <c>DependsOn</c>
/// edges; the SERVER checks the DAG is well-formed BEFORE it drives the dependency gate. A dangling reference (a
/// dependency on a subtask the plan never declares) or a CYCLE can never be satisfied, so without this the gate would
/// defer those subtasks forever and the run would spin on empty spawns until the no-progress bound — a slow, opaque
/// stall. The validator force-STOPs at plan time with a legible <see cref="SupervisorStopReasons.PlanInvalid"/> instead.
///
/// <para>Pure + deterministic over the decision (replay re-derives the identical verdict). A FLAT plan (no
/// <c>DependsOn</c> on any subtask) has no edges to validate ⇒ always valid ⇒ byte-identical to before. Only structural
/// CONTRADICTIONS are rejected; a well-formed DAG (including diamonds and deep chains) passes.</para>
/// </summary>
public static class SupervisorPlanValidator
{
    /// <summary>
    /// The terminal <see cref="SupervisorStopReasons.PlanInvalid"/> reason when a <c>plan</c> decision's <c>DependsOn</c>
    /// graph is structurally invalid (a dangling reference or a cycle), or null to proceed. A non-plan decision, a flat
    /// plan (no edges), and a well-formed DAG all return null. A malformed payload returns null (defensive — the
    /// canonical payload always parses; a parse failure is a deeper bug the downstream surfaces, not a plan-shape error).
    /// </summary>
    public static string? Validate(SupervisorDecision decision)
    {
        if (decision.Kind != SupervisorDecisionKinds.Plan) return null;

        IReadOnlyList<SupervisorPlannedSubtask> subtasks;
        try { subtasks = JsonSerializer.Deserialize<SupervisorPlanPayload>(decision.PayloadJson, AgentJson.Options)?.Subtasks ?? Array.Empty<SupervisorPlannedSubtask>(); }
        catch (JsonException) { return null; }

        // The declared subtask universe (duplicate ids collapse — a dup is a degenerate flat-plan case, not this gate's concern).
        var declared = subtasks.Select(s => s.Id).ToHashSet();

        // The edge set, keyed by subtask (first id wins on a dup, mirroring SupervisorDependencyGate). No DependsOn anywhere ⇒ a flat plan ⇒ trivially valid.
        var edges = new Dictionary<string, IReadOnlyList<string>>();

        foreach (var subtask in subtasks)
            if (subtask.DependsOn is { Count: > 0 } deps && !edges.ContainsKey(subtask.Id))
                edges[subtask.Id] = deps;

        if (edges.Count == 0) return null;

        // Dangling reference: a dependency on a subtask the plan never declares — the gate could never satisfy it.
        foreach (var deps in edges.Values)
            foreach (var dep in deps)
                if (!declared.Contains(dep))
                    return SupervisorStopReasons.PlanInvalid;

        // Cycle: a back-edge in the DependsOn DAG — its members can never all be satisfied (includes a self-loop A→A).
        return HasCycle(declared, edges) ? SupervisorStopReasons.PlanInvalid : null;
    }

    /// <summary>True when the DependsOn graph has a cycle, by DFS with three-colour marking (white = unvisited, grey = on the current path, black = done). A grey re-entry is a back-edge ⇒ a cycle. Bounded by the schema's ≤20 subtasks, so the recursion can't blow the stack.</summary>
    private static bool HasCycle(IReadOnlySet<string> nodes, IReadOnlyDictionary<string, IReadOnlyList<string>> edges)
    {
        var colour = new Dictionary<string, int>();   // absent = white, 1 = grey, 2 = black

        bool Visit(string node)
        {
            if (colour.TryGetValue(node, out var c)) return c == 1;   // grey → back-edge (cycle); black → already cleared

            colour[node] = 1;

            if (edges.TryGetValue(node, out var deps))
                foreach (var dep in deps)
                    if (Visit(dep)) return true;

            colour[node] = 2;
            return false;
        }

        foreach (var node in nodes)
            if (Visit(node)) return true;

        return false;
    }
}
