using CodeSpace.Messages.Plans;

namespace CodeSpace.Core.Services.Plans;

/// <summary>
/// Pure structural validation of a plan's item DAG — the graph-tier twin of the supervisor's
/// <c>SupervisorPlanValidator</c> (which validates the loop-tier plan decision). A structurally contradictory
/// plan (duplicate ids, a dependsOn referencing an undeclared item, a cycle) can never execute in order, so
/// the producer fails CLOSED at authoring time instead of a consumer stalling on it later. Optional-free
/// plans (no dependsOn anywhere) pass untouched.
/// </summary>
public static class WorkPlanItemGraph
{
    /// <summary>The structural error message, or null when the item graph is a well-formed DAG.</summary>
    public static string? Validate(IReadOnlyList<WorkPlanItem> items)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in items)
            if (!ids.Add(item.Id))
                return $"The plan declares the item id '{item.Id}' more than once.";

        foreach (var item in items)
            foreach (var dep in item.DependsOn ?? Array.Empty<string>())
                if (!ids.Contains(dep))
                    return $"Item '{item.Id}' depends on '{dep}', which the plan does not declare.";

        return FindCycle(items);
    }

    /// <summary>Iterative 3-colour DFS over the dependsOn edges — returns a cycle message or null.</summary>
    private static string? FindCycle(IReadOnlyList<WorkPlanItem> items)
    {
        var edges = items.ToDictionary(i => i.Id, i => i.DependsOn ?? (IReadOnlyList<string>)Array.Empty<string>(), StringComparer.Ordinal);
        var state = new Dictionary<string, int>(StringComparer.Ordinal);   // 0/absent = white, 1 = grey (on stack), 2 = black

        foreach (var root in edges.Keys)
        {
            if (state.TryGetValue(root, out var s) && s == 2) continue;

            var stack = new Stack<(string Id, int Next)>();
            stack.Push((root, 0));
            state[root] = 1;

            while (stack.Count > 0)
            {
                var (id, next) = stack.Pop();
                var deps = edges[id];

                if (next < deps.Count)
                {
                    stack.Push((id, next + 1));
                    var dep = deps[next];

                    if (!state.TryGetValue(dep, out var depState))
                    {
                        state[dep] = 1;
                        stack.Push((dep, 0));
                    }
                    else if (depState == 1)
                    {
                        return $"The plan's dependencies contain a cycle through '{dep}'.";
                    }
                }
                else
                {
                    state[id] = 2;
                }
            }
        }

        return null;
    }
}
