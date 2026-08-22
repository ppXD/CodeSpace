using CodeSpace.Core.Handlers.QueryHandlers.Tasks;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Tasks.Phases;
using CodeSpace.Core.Services.Tasks.Timeline;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Display;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Tasks;
using CodeSpace.Messages.Tasks.Phases;
using CodeSpace.Messages.Tasks.Timeline;
using Shouldly;

namespace CodeSpace.UnitTests.Handlers.Tasks;

[Trait("Category", "Unit")]
public sealed class WorkflowRunObservationEnvelopeHandlerTests
{
    private static readonly Guid RunId = Guid.NewGuid();
    private static readonly Guid TeamId = Guid.NewGuid();

    public static IEnumerable<object[]> Statuses() => Enum.GetValues<WorkflowRunStatus>().Select(status => new object[] { status });

    [Theory]
    [MemberData(nameof(Statuses))]
    public async Task Timeline_envelope_preserves_every_status_name_without_loading_full_detail(WorkflowRunStatus status)
    {
        var identity = new Identity(status);
        var handler = new GetRunTimelineQueryHandler(new Timeline(), identity, new Team());

        var response = await handler.Handle(new GetRunTimelineQuery { RunId = RunId }, CancellationToken.None);

        response.ShouldNotBeNull();
        response!.RunStatus.ShouldBe(status.ToString());
        response.Events.ShouldBeEmpty();
        identity.Calls.ShouldBe(1);
        FullDetailIsNotAConstructorDependency(typeof(GetRunTimelineQueryHandler));
    }

    [Theory]
    [MemberData(nameof(Statuses))]
    public async Task Phase_envelope_preserves_every_status_name_without_loading_full_detail(WorkflowRunStatus status)
    {
        var identity = new Identity(status);
        var handler = new GetTaskRunPhasesQueryHandler(new Phases(), identity, new Team());

        var response = await handler.Handle(new GetTaskRunPhasesQuery { RunId = RunId }, CancellationToken.None);

        response.ShouldNotBeNull();
        response!.RunStatus.ShouldBe(status.ToString());
        response.Phases.ShouldBeEmpty();
        identity.Calls.ShouldBe(1);
        FullDetailIsNotAConstructorDependency(typeof(GetTaskRunPhasesQueryHandler));
    }

    private static void FullDetailIsNotAConstructorDependency(Type handler)
    {
        var dependencies = handler.GetConstructors().ShouldHaveSingleItem().GetParameters().Select(parameter => parameter.ParameterType).ToList();
        dependencies.ShouldContain(typeof(IWorkflowRunObservationIdentityBundle));
        dependencies.ShouldNotContain(typeof(IWorkflowService));
    }

    private sealed class Identity : IWorkflowRunObservationIdentityBundle
    {
        private readonly WorkflowRunStatus _status;
        public Identity(WorkflowRunStatus status) { _status = status; }
        public int Calls { get; private set; }

        public Task<WorkflowRunObservationIdentity?> GetAsync(Guid teamId, Guid runId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<WorkflowRunObservationIdentity?>(teamId == TeamId && runId == RunId
                ? new WorkflowRunObservationIdentity(runId, 11, _status)
                : null);
        }
    }

    private sealed class Timeline : IRunTimelineProjector
    {
        public Task<IReadOnlyList<RunTimelineEvent>?> ProjectAsync(Guid runId, Guid teamId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RunTimelineEvent>?>(Array.Empty<RunTimelineEvent>());
    }

    private sealed class Phases : IRunPhaseProjector
    {
        public Task<IReadOnlyList<RunPhase>?> ProjectAsync(Guid runId, Guid teamId, CancellationToken cancellationToken, bool mergeLineage = true) =>
            Task.FromResult<IReadOnlyList<RunPhase>?>(Array.Empty<RunPhase>());
    }

    private sealed class Team : ICurrentTeam
    {
        public Guid? Id => TeamId;
        public bool IsSet => true;
    }
}
