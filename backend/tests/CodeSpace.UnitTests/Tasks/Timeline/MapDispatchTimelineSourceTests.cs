using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Display;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks.Timeline;
using Shouldly;

namespace CodeSpace.UnitTests.Tasks.Timeline;

[Trait("Category", "Unit")]
public sealed class MapDispatchTimelineSourceTests
{
    private static readonly Guid RunId = Guid.NewGuid();
    private static readonly Guid TeamId = Guid.NewGuid();
    private static readonly Guid AgentRunId = Guid.NewGuid();
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1);

    [Fact]
    public void Source_uses_the_body_blind_metadata_reader_and_not_full_workflow_detail()
    {
        var dependencies = typeof(MapDispatchTimelineSource).GetConstructors().ShouldHaveSingleItem().GetParameters().Select(value => value.ParameterType).ToList();

        dependencies.ShouldContain(typeof(IWorkflowRunViewMetadataReader));
        dependencies.ShouldNotContain(typeof(IWorkflowService));
    }

    [Fact]
    public async Task Available_map_metadata_preserves_the_dispatch_beat()
    {
        var source = new MapDispatchTimelineSource(new StubMetadataReader(Metadata(cells:
        [
            Cell("fan", "", NodeStatus.Running, startedAt: StartedAt),
            Cell("worker", "fan#0", NodeStatus.Running, MapFanout.ContainerKind, AgentRunId),
        ])));

        var events = await source.ContributeAsync(Context(), CancellationToken.None);

        var item = events.ShouldHaveSingleItem();
        item.Id.ShouldBe("map-dispatch-fan");
        item.Kind.ShouldBe(MapDispatchTimelineMap.DispatchKind);
        item.Title.ShouldBe("Dispatched 1 agent");
        item.Summary.ShouldBeNull();
        item.Severity.ShouldBe(TimelineSeverity.Info);
        item.OccurredAt.ShouldBe(StartedAt);
        item.NodeId.ShouldBe("fan");
    }

    [Fact]
    public async Task Failed_map_uses_a_bounded_pointer_to_the_ledger_error_instead_of_loading_the_run_body()
    {
        var source = new MapDispatchTimelineSource(new StubMetadataReader(Metadata(cells:
        [
            Cell("fan", "", NodeStatus.Failure, startedAt: StartedAt),
            Cell("worker", "fan#0", NodeStatus.Failure, MapFanout.ContainerKind, AgentRunId),
        ])));

        var item = (await source.ContributeAsync(Context(), CancellationToken.None)).ShouldHaveSingleItem();

        item.Severity.ShouldBe(TimelineSeverity.Error);
        item.Summary.ShouldBe("The map failed; its recorded error remains available on the node.failed timeline event and Trace.");
    }

    [Fact]
    public async Task Failed_map_that_staged_no_agent_preserves_the_empty_fanout_explanation()
    {
        var source = new MapDispatchTimelineSource(new StubMetadataReader(Metadata(cells:
        [
            Cell("fan", "", NodeStatus.Failure, startedAt: StartedAt),
            Cell("worker", "fan#0", NodeStatus.Failure, MapFanout.ContainerKind),
        ])));

        var item = (await source.ContributeAsync(Context(), CancellationToken.None)).ShouldHaveSingleItem();

        item.Title.ShouldBe("Dispatched no agents");
        item.Summary.ShouldBe("No agent was dispatched — this map fanned out no branch.");
        item.Severity.ShouldBe(TimelineSeverity.Error);
    }

    [Theory]
    [InlineData(WorkflowRunViewAvailability.Truncated, "partially available")]
    [InlineData(WorkflowRunViewAvailability.TooLarge, "unavailable")]
    [InlineData(WorkflowRunViewAvailability.Corrupt, "unavailable")]
    [InlineData(WorkflowRunViewAvailability.Unavailable, "unavailable")]
    public async Task Incomplete_cell_metadata_emits_a_visible_coverage_warning(WorkflowRunViewAvailability availability, string expected)
    {
        var source = new MapDispatchTimelineSource(new StubMetadataReader(Metadata(cells: [], cellsAvailability: availability)));

        var item = (await source.ContributeAsync(Context(), CancellationToken.None)).ShouldHaveSingleItem();

        item.Id.ShouldBe("map-dispatch-coverage");
        item.Kind.ShouldBe("observation.coverage");
        item.Title.ShouldContain(expected, Case.Insensitive);
        item.Severity.ShouldBe(TimelineSeverity.Warning);
        item.Level.ShouldBe(TimelineLevel.Milestone);
        item.SourceKey.ShouldBe(MapDispatchTimelineMap.Key);
    }

    [Fact]
    public async Task Corrupt_topology_takes_precedence_over_a_truncated_cell_window()
    {
        var source = new MapDispatchTimelineSource(new StubMetadataReader(Metadata(
            cells: [],
            cellsAvailability: WorkflowRunViewAvailability.Truncated,
            topologyAvailability: WorkflowRunViewAvailability.Corrupt)));

        var item = (await source.ContributeAsync(Context(), CancellationToken.None)).ShouldHaveSingleItem();

        item.Title.ShouldBe("Map dispatch history unavailable");
        item.Summary.ShouldContain("could not be read safely", Case.Insensitive);
    }

    [Fact]
    public async Task Metadata_backend_fault_remains_an_infrastructure_failure()
    {
        var source = new MapDispatchTimelineSource(new StubMetadataReader(new IOException("metadata backend unavailable")));

        var error = await Should.ThrowAsync<IOException>(() => source.ContributeAsync(Context(), CancellationToken.None));

        error.Message.ShouldBe("metadata backend unavailable");
    }

    private static RunTimelineContext Context() => new() { RunId = RunId, TeamId = TeamId };

    private static WorkflowRunViewMetadata Metadata(IReadOnlyList<WorkflowRunCellMetadata> cells, WorkflowRunViewAvailability cellsAvailability = WorkflowRunViewAvailability.Available,
        WorkflowRunViewAvailability topologyAvailability = WorkflowRunViewAvailability.Available) => new()
    {
        RunId = RunId,
        RunNumber = 7,
        SourceType = "Snapshot",
        Status = WorkflowRunStatus.Running,
        HasError = false,
        CreatedDate = StartedAt.AddSeconds(-1),
        StartedAt = StartedAt,
        Scope = WorkflowRunViewScope.LineageMerged,
        CellsAvailability = cellsAvailability,
        LinksAvailability = WorkflowRunViewAvailability.Available,
        Cells = cells,
        TopologyAvailability = topologyAvailability,
        Topology = new WorkflowRunCanvasTopology
        {
            Nodes = [new WorkflowRunCanvasNode { Id = "fan", TypeKey = MapFanout.ContainerKind }],
            Edges = [],
        },
    };

    private static WorkflowRunCellMetadata Cell(string nodeId, string iterationKey, NodeStatus status, string? containerKind = null,
        Guid? agentRunId = null, DateTimeOffset? startedAt = null) => new()
    {
        SourceRunId = RunId,
        NodeId = nodeId,
        IterationKey = iterationKey,
        ContainerKind = containerKind,
        Status = status,
        StartedAt = startedAt,
        AgentRunId = agentRunId,
        RerunnableFromHere = false,
    };

    private sealed class StubMetadataReader : IWorkflowRunViewMetadataReader
    {
        private readonly WorkflowRunViewMetadata? _metadata;
        private readonly Exception? _failure;

        public StubMetadataReader(WorkflowRunViewMetadata metadata) { _metadata = metadata; }
        public StubMetadataReader(Exception failure) { _failure = failure; }

        public Task<WorkflowRunViewMetadata?> ReadAsync(Guid runId, Guid teamId, WorkflowRunViewScope scope, CancellationToken cancellationToken)
        {
            if (_failure is not null) throw _failure;
            return Task.FromResult(runId == RunId && teamId == TeamId && scope == WorkflowRunViewScope.LineageMerged ? _metadata : null);
        }
    }
}
