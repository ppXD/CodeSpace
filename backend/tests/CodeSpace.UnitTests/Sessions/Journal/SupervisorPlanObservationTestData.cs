using CodeSpace.Core.Services.Supervisor.Observation;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Workflows.Supervisor;

namespace CodeSpace.UnitTests.Sessions.Journal;

internal sealed class FakeSupervisorPlanObservationPageBundle : ISupervisorPlanObservationPageBundle
{
    public required SupervisorPlanObservationPage? Page { get; init; }
    public Exception? Error { get; init; }
    public int Calls { get; private set; }

    public Task<SupervisorPlanObservationPage?> GetForRunAsync(Guid supervisorRunId, Guid teamId, CancellationToken cancellationToken)
    {
        Calls++;
        if (Error is not null) return Task.FromException<SupervisorPlanObservationPage?>(Error);
        return Task.FromResult(Page);
    }
}

internal static class SupervisorPlanObservationTestData
{
    internal static SupervisorPlanObservationPage Page(bool hasMore = false, params SupervisorPlanObservationItem[] items)
    {
        var runId = items.FirstOrDefault()?.Metadata.SupervisorRunId ?? Guid.NewGuid();
        var scoped = items.Select(item => item with { Metadata = item.Metadata with { SupervisorRunId = runId } }).ToList();
        return new SupervisorPlanObservationPage
        {
            SupervisorRunId = runId,
            Mode = SupervisorDecisionObservationStoryPageMode.Tail.ToString(),
            Limit = SupervisorPlanObservationPageBundle.PageLimit,
            SnapshotRevision = items.Length,
            HeadRevision = items.Length,
            Items = scoped,
            HasMore = hasMore,
            NextNewerCursor = "test-only",
        };
    }

    internal static SupervisorPlanObservationItem Item(SupervisorPlanObservationItemSpec? spec = null)
    {
        spec ??= new SupervisorPlanObservationItemSpec();
        var runId = Guid.NewGuid();
        return new SupervisorPlanObservationItem
        {
            Metadata = new SupervisorDecisionObservationMetadata
            {
                DecisionId = Guid.NewGuid(),
                SupervisorRunId = runId,
                DecisionKind = SupervisorDecisionKinds.Plan,
                Status = spec.Status,
                StoryOrder = spec.StoryOrder,
                ObservationRevision = spec.StoryOrder,
                CreatedAt = DateTimeOffset.UnixEpoch,
                LastModifiedAt = DateTimeOffset.UnixEpoch,
                ErrorTotalBytes = 0,
                ErrorState = SupervisorDecisionObservationErrorState.None,
            },
            SubtasksState = spec.SubtasksState,
            SubtasksTotalCount = spec.SubtaskTotal,
            SubtasksOmittedCount = spec.SubtaskOmitted,
            Subtasks = spec.Subtasks ?? [Subtask("s1", "Exact title")],
            ModelUsageState = spec.ModelUsageState,
            ModelUsage = spec.ModelUsage,
        };
    }

    internal static SupervisorPlanSubtaskObservationLeaf Subtask(string id, string title, int? idBytes = null, int? titleBytes = null) => new()
    {
        IdPrefix = id,
        IdTotalBytes = idBytes ?? System.Text.Encoding.UTF8.GetByteCount(id),
        TitlePrefix = title,
        TitleTotalBytes = titleBytes ?? System.Text.Encoding.UTF8.GetByteCount(title),
    };

    internal static SupervisorPlanModelUsageObservationLeaf Usage(string model = "metis-coder-plus", int? modelBytes = null) => new()
    {
        ModelPrefix = model,
        ModelTotalBytes = modelBytes ?? System.Text.Encoding.UTF8.GetByteCount(model),
        InputTokens = 1_000,
        OutputTokens = 200,
    };
}

internal sealed record SupervisorPlanObservationItemSpec
{
    public SupervisorPlanObservationLeafState SubtasksState { get; init; } = SupervisorPlanObservationLeafState.Exact;
    public SupervisorPlanObservationLeafState ModelUsageState { get; init; } = SupervisorPlanObservationLeafState.Missing;
    public SupervisorDecisionObservationStatus Status { get; init; } = SupervisorDecisionObservationStatus.Succeeded;
    public int StoryOrder { get; init; } = 1;
    public int SubtaskTotal { get; init; } = 1;
    public int SubtaskOmitted { get; init; }
    public IReadOnlyList<SupervisorPlanSubtaskObservationLeaf>? Subtasks { get; init; }
    public SupervisorPlanModelUsageObservationLeaf? ModelUsage { get; init; }
}
