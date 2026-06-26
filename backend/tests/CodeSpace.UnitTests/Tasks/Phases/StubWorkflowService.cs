using System.Text.Json;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Dtos.Workflows;

namespace CodeSpace.UnitTests.Tasks.Phases;

/// <summary>
/// A unit test double for <see cref="IWorkflowService"/> that returns ONLY a pre-seeded <see cref="WorkflowRunDetail"/>
/// from <see cref="GetRunAsync"/> (team-scoped: a foreign team gets null) and throws for every other member — the
/// phase node source only uses the run-detail read. The seed is keyed by <c>(runId, teamId)</c> so a foreign-team
/// lookup conflates to null, mirroring the real service.
/// </summary>
internal sealed class StubWorkflowService : IWorkflowService
{
    private readonly Guid _runId;
    private readonly Guid _teamId;
    private readonly WorkflowRunDetail? _detail;

    public StubWorkflowService(Guid runId, Guid teamId, WorkflowRunDetail? detail)
    {
        _runId = runId;
        _teamId = teamId;
        _detail = detail;
    }

    public Task<WorkflowRunDetail?> GetRunAsync(Guid runId, Guid teamId, CancellationToken cancellationToken) =>
        Task.FromResult(runId == _runId && teamId == _teamId ? _detail : null);

    public Task<IReadOnlyList<WorkflowSummary>> ListAsync(Guid teamId, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task<WorkflowDetail?> GetAsync(Guid workflowId, Guid teamId, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task<Guid> CreateAsync(Guid teamId, string name, string? description, WorkflowDefinition definition, IReadOnlyList<WorkflowActivationInput> activations, bool enabled, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task UpdateAsync(Guid workflowId, Guid teamId, string name, string? description, WorkflowDefinition definition, IReadOnlyList<WorkflowActivationInput> activations, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task DeleteAsync(Guid workflowId, Guid teamId, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task SetEnabledAsync(Guid workflowId, Guid teamId, bool enabled, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task<Guid> RunManuallyAsync(Guid workflowId, Guid teamId, Guid actorUserId, JsonElement? payload, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task<Guid> ReplayRunAsync(Guid originalRunId, Guid teamId, Guid actorUserId, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task<Guid> RerunFromNodeAsync(Guid originalRunId, string fromNodeId, Guid teamId, Guid actorUserId, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task<Guid> RerunMapBranchAsync(Guid originalRunId, string mapNodeId, int branchIndex, Guid teamId, Guid actorUserId, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task<Guid> RerunMapBranchesAsync(Guid originalRunId, string mapNodeId, IReadOnlySet<int> branchIndices, Guid teamId, Guid actorUserId, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task<IReadOnlyList<WorkflowRunSummary>> ListRunsAsync(Guid workflowId, Guid teamId, int limit, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task<RunPage> ListTeamRunsAsync(Guid teamId, RunListFilter filter, string? cursor, int limit, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task<RunPage> ListTeamRunsPageAsync(Guid teamId, RunListFilter filter, int page, int pageSize, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task<RunSummary> SummarizeTeamRunsAsync(Guid teamId, RunListFilter filter, DateTimeOffset todayStart, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task<bool> ApproveRunAsync(Guid runId, Guid teamId, Guid actorUserId, bool approved, string? comment, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task<CancelRunOutcome?> CancelRunAsync(Guid runId, Guid teamId, CancellationToken cancellationToken) => throw new NotImplementedException();
    public IReadOnlyList<NodeManifestDto> ListNodeManifests() => throw new NotImplementedException();
    public IReadOnlyList<SystemVariableDto> ListSystemVariables() => throw new NotImplementedException();
}
