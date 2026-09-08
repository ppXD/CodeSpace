using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Workflows.Engine;

/// <summary>Folds engine-owned map counters into the workflow's honest terminal claim.</summary>
internal static class MapTerminalOutcome
{
    internal static WorkflowTerminalDisposition Classify(IReadOnlyList<MapCompletionFacts> maps)
    {
        foreach (var map in maps)
        {
            if (map.Count is null || map.Failed is null || map.Count < 0 || map.Failed < 0 || map.Failed > map.Count)
                return new WorkflowTerminalDisposition(WorkflowRunStatus.Failure, $"Map '{map.NodeId}' reported invalid completion counters.", null);

            if (map.Count > 0 && map.Failed == map.Count)
                return new WorkflowTerminalDisposition(WorkflowRunStatus.Failure, $"All {map.Count} branches failed in map '{map.NodeId}'.", WorkflowRunOutcomes.AllBranchesFailed);
        }

        return maps.Any(map => map.Failed > 0)
            ? new WorkflowTerminalDisposition(WorkflowRunStatus.Success, null, WorkflowRunOutcomes.PartialFailure)
            : new WorkflowTerminalDisposition(WorkflowRunStatus.Success, null, null);
    }
}

internal readonly record struct MapCompletionFacts(string NodeId, int? Count, int? Failed);
internal readonly record struct WorkflowTerminalDisposition(WorkflowRunStatus Status, string? Error, string? Outcome);
