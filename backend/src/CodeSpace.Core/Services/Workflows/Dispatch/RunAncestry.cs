using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Workflows.Dispatch;

/// <summary>
/// The sub-workflow ancestry walk the dispatcher refuses a run by, the same walk <c>ExecutionAuthorityService</c> denies an
/// agent's authority by: up the parent chain while each run is a sub-workflow child, refusing at the first ancestor that has
/// finished. Only a child's parent can be waiting for it; a rerun's or replay's parent is its lineage, finished by definition,
/// so the walk ends at the first run that is not a child.
/// </summary>
internal static class RunAncestry
{
    /// <summary>One run on the walk: what kind of run it is, the run it hangs under, and how far it has got.</summary>
    internal sealed record Hop(string SourceType, Guid? ParentRunId, WorkflowRunStatus Status);

    /// <summary>The most child hops a walk takes. Nesting is capped at <see cref="SubworkflowService.MaxDepth"/>, so a longer chain of children is not one the engine staged.</summary>
    internal const int MaxChildHops = SubworkflowService.MaxDepth;

    /// <summary>
    /// Whether some ancestor of <paramref name="runId"/>, reached through sub-workflow children only, has finished — or is missing,
    /// loops back, or lies deeper than nesting allows, none of which the engine stages. <paramref name="load"/> reads one run, null when absent.
    /// </summary>
    internal static async Task<bool> HasFinishedAncestorAsync(Guid runId, Func<Guid, Task<Hop?>> load)
    {
        var lineage = new HashSet<Guid> { runId };
        var run = await load(runId).ConfigureAwait(false);

        for (var hops = 0; run is { SourceType: WorkflowRunSourceTypes.ChildWorkflow, ParentRunId: { } parentId }; hops++)
        {
            if (hops == MaxChildHops || !lineage.Add(parentId)) return true;

            run = await load(parentId).ConfigureAwait(false);

            if (run is null || WorkflowRunState.IsTerminal(run.Status)) return true;
        }

        return false;
    }
}
