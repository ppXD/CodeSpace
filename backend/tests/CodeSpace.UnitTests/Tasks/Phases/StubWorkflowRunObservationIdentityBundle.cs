using CodeSpace.Core.Services.Workflows.Display;
using CodeSpace.Messages.Enums;

namespace CodeSpace.UnitTests.Tasks.Phases;

internal sealed class StubWorkflowRunObservationIdentityBundle : IWorkflowRunObservationIdentityBundle
{
    private readonly Guid _runId;
    private readonly Guid _teamId;
    private readonly WorkflowRunObservationIdentity? _identity;
    private readonly Exception? _failure;

    public StubWorkflowRunObservationIdentityBundle(Guid runId, Guid teamId, WorkflowRunStatus? status, Exception? failure = null)
    {
        _runId = runId;
        _teamId = teamId;
        _identity = status is null ? null : new WorkflowRunObservationIdentity(runId, 7, status.Value);
        _failure = failure;
    }

    public int Calls { get; private set; }

    public Task<WorkflowRunObservationIdentity?> GetAsync(Guid teamId, Guid runId, CancellationToken cancellationToken)
    {
        Calls++;
        if (_failure is not null) throw _failure;
        return Task.FromResult(teamId == _teamId && runId == _runId ? _identity : null);
    }
}
