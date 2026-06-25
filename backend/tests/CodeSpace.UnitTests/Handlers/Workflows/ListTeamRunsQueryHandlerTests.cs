using System.Text.Json;
using CodeSpace.Core.Handlers.QueryHandlers.Workflows;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Workflows;
using Shouldly;

namespace CodeSpace.UnitTests.Handlers.Workflows;

/// <summary>
/// Unit-proves <see cref="ListTeamRunsQueryHandler"/> is a thin dispatcher (Rule 16): it scopes to the CALLER'S team
/// (<see cref="ICurrentTeam"/>, never the wire), folds the bound filter via <c>ToFilter()</c>, and ROUTES on the
/// presence of a page number — a <c>Page</c> selects OFFSET (numbered) pagination, its absence keeps KEYSET (the live
/// feed). No DbContext; the routing decision is the whole behaviour.
/// </summary>
[Trait("Category", "Unit")]
public class ListTeamRunsQueryHandlerTests
{
    [Fact]
    public async Task A_page_number_routes_to_offset_pagination_scoped_to_the_callers_team()
    {
        var teamId = Guid.NewGuid();
        var svc = new CapturingService();

        await new ListTeamRunsQueryHandler(svc, new StubCurrentTeam(teamId))
            .Handle(new ListTeamRunsQuery { Page = 3, Limit = 20, Cursor = "ignored", Statuses = new[] { WorkflowRunStatus.Success } }, CancellationToken.None);

        svc.OffsetCall.ShouldNotBeNull("a page number selects OFFSET (numbered) pagination");
        svc.KeysetCall.ShouldBeNull("the keyset path must NOT run when a page is given");
        svc.OffsetCall!.Value.teamId.ShouldBe(teamId, "scoped to the caller's team (ICurrentTeam), never the wire");
        svc.OffsetCall.Value.page.ShouldBe(3);
        svc.OffsetCall.Value.pageSize.ShouldBe(20, "Limit is the page size in offset mode");
        svc.OffsetCall.Value.filter.Statuses.ShouldBe(new[] { WorkflowRunStatus.Success }, "the bound filter folds through ToFilter()");
    }

    [Fact]
    public async Task No_page_number_routes_to_keyset_pagination_with_the_cursor()
    {
        var teamId = Guid.NewGuid();
        var svc = new CapturingService();

        await new ListTeamRunsQueryHandler(svc, new StubCurrentTeam(teamId))
            .Handle(new ListTeamRunsQuery { Cursor = "c1", Limit = 50 }, CancellationToken.None);

        svc.KeysetCall.ShouldNotBeNull("no page number → keyset (the live feed)");
        svc.OffsetCall.ShouldBeNull();
        svc.KeysetCall!.Value.teamId.ShouldBe(teamId);
        svc.KeysetCall.Value.cursor.ShouldBe("c1", "the cursor threads through in keyset mode");
        svc.KeysetCall.Value.limit.ShouldBe(50);
    }

    private sealed class CapturingService : IWorkflowService
    {
        public (Guid teamId, RunListFilter filter, string? cursor, int limit)? KeysetCall { get; private set; }
        public (Guid teamId, RunListFilter filter, int page, int pageSize)? OffsetCall { get; private set; }

        public Task<RunPage> ListTeamRunsAsync(Guid teamId, RunListFilter filter, string? cursor, int limit, CancellationToken cancellationToken)
        {
            KeysetCall = (teamId, filter, cursor, limit);
            return Task.FromResult(new RunPage { Items = Array.Empty<WorkflowRunSummary>() });
        }

        public Task<RunPage> ListTeamRunsPageAsync(Guid teamId, RunListFilter filter, int page, int pageSize, CancellationToken cancellationToken)
        {
            OffsetCall = (teamId, filter, page, pageSize);
            return Task.FromResult(new RunPage { Items = Array.Empty<WorkflowRunSummary>(), TotalCount = 0 });
        }

        // ── the rest of IWorkflowService is unused by this handler ──
        public Task<RunSummary> SummarizeTeamRunsAsync(Guid teamId, RunListFilter filter, DateTimeOffset todayStart, CancellationToken cancellationToken) => throw new NotImplementedException();
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
        public Task<IReadOnlyList<WorkflowRunSummary>> ListRunsAsync(Guid workflowId, Guid teamId, int limit, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<WorkflowRunDetail?> GetRunAsync(Guid runId, Guid teamId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<bool> ApproveRunAsync(Guid runId, Guid teamId, Guid actorUserId, bool approved, string? comment, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<CancelRunOutcome?> CancelRunAsync(Guid runId, Guid teamId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public IReadOnlyList<NodeManifestDto> ListNodeManifests() => throw new NotImplementedException();
        public IReadOnlyList<SystemVariableDto> ListSystemVariables() => throw new NotImplementedException();
    }

    private sealed class StubCurrentTeam : ICurrentTeam
    {
        public StubCurrentTeam(Guid? id) { Id = id; }

        public Guid? Id { get; }
        public bool IsSet => Id is not null;
    }
}
