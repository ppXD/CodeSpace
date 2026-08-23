using CodeSpace.Core.Services.Tasks.Timeline;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Display;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks.Timeline;
using Shouldly;

namespace CodeSpace.UnitTests.Tasks.Timeline;

[Trait("Category", "Unit")]
public sealed class MapPlannerTimelineSourceTests
{
    private static readonly Guid RunId = Guid.NewGuid();
    private static readonly Guid TeamId = Guid.NewGuid();
    private static readonly DateTimeOffset At = DateTimeOffset.UtcNow;

    [Fact]
    public void Source_uses_the_shared_bounded_plan_bundle_and_not_full_detail_or_inflation()
    {
        var dependencies = typeof(MapPlannerTimelineSource).GetConstructors().ShouldHaveSingleItem().GetParameters().Select(value => value.ParameterType).ToList();

        dependencies.ShouldContain(typeof(IWorkflowMapPlanObservationBundle));
        dependencies.ShouldNotContain(typeof(IWorkflowService));
        dependencies.ShouldNotContain(typeof(IRunNodeOutputInflater));
    }

    [Fact]
    public async Task Incomplete_leaf_emits_coverage_instead_of_a_false_plan_count()
    {
        var source = new MapPlannerTimelineSource(new StubBundle(Observation(WorkflowMapPlanLeafState.Truncated, 25)));

        var item = (await source.ContributeAsync(new RunTimelineContext { RunId = RunId, TeamId = TeamId }, CancellationToken.None)).ShouldHaveSingleItem();

        item.Kind.ShouldBe("observation.coverage");
        item.Title.ShouldBe("Map plan partially available");
        item.Summary.ShouldContain("no partial plan", Case.Insensitive);
    }

    private static WorkflowMapPlanObservation Observation(WorkflowMapPlanLeafState state, int count) => new()
    {
        RunId = RunId,
        Availability = WorkflowRunViewAvailability.Available,
        AnchorAt = At,
        Planners = [new WorkflowMapPlannerObservation
        {
            ProducerNodeId = "planner", Status = NodeStatus.Success, CompletedAt = At, StateRecordId = Guid.NewGuid(), StateRecordSequence = 9,
            ErrorState = WorkflowMapPlanLeafState.Missing, SubtasksState = state, SubtasksTotalCount = count,
            ModelUsageState = WorkflowMapPlanLeafState.Missing,
        }],
    };

    private sealed class StubBundle : IWorkflowMapPlanObservationBundle
    {
        private readonly WorkflowMapPlanObservation _observation;
        public StubBundle(WorkflowMapPlanObservation observation) { _observation = observation; }
        public Task<WorkflowMapPlanObservation?> GetAsync(Guid runId, Guid teamId, CancellationToken cancellationToken) =>
            Task.FromResult<WorkflowMapPlanObservation?>(runId == RunId && teamId == TeamId ? _observation : null);
    }
}
