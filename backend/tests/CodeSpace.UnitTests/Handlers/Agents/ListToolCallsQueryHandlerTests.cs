using CodeSpace.Core.Handlers.QueryHandlers.Agents;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Queries.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Handlers.Agents;

/// <summary>
/// Unit-proves <see cref="ListToolCallsQueryHandler"/> is a thin dispatcher (Rule 16): it delegates to
/// <see cref="IToolCallAuditReader.ListForRunAsync"/> with the CALLER'S team (never the DbContext). The reader owns
/// the body-free database projection and chronological ordering; the handler must not fall back to the execution
/// ledger's full-entity replay read, because that materializes every potentially-large <c>ResultJson</c> body.
/// </summary>
[Trait("Category", "Unit")]
public class ListToolCallsQueryHandlerTests
{
    [Fact]
    public async Task Delegates_to_the_service_with_the_callers_team_and_run()
    {
        var teamId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var reader = new CapturingAuditReader(Array.Empty<ToolCallView>());

        await new ListToolCallsQueryHandler(reader, new StubCurrentTeam(teamId))
            .Handle(new ListToolCallsQuery { AgentRunId = runId }, CancellationToken.None);

        reader.LastRunId.ShouldBe(runId, "the handler must pass the requested run id through");
        reader.LastTeamId.ShouldBe(teamId, "the handler must scope the read to the CALLER's team (ICurrentTeam), not the wire");
    }

    [Fact]
    public async Task Returns_the_body_free_reader_projection_without_remapping_or_reordering()
    {
        var teamId = Guid.NewGuid();
        var approverId = Guid.NewGuid();
        var older = DateTimeOffset.UtcNow.AddMinutes(-5);
        var newer = DateTimeOffset.UtcNow.AddMinutes(-1);
        var approvedAt = newer.AddSeconds(20);

        IReadOnlyList<ToolCallView> rows = new[]
        {
            new ToolCallView { ToolKind = "git.open_pr", Status = ToolCallLedgerStatus.Failed, CreatedDate = older, LastModifiedDate = older, Error = "boom", ApprovedByUserId = null, ApprovedAt = null },
            new ToolCallView { ToolKind = "git.merge_pr", Status = ToolCallLedgerStatus.Succeeded, CreatedDate = newer, LastModifiedDate = approvedAt, Error = null, ApprovedByUserId = approverId, ApprovedAt = approvedAt },
        };
        var reader = new CapturingAuditReader(rows);

        var result = await new ListToolCallsQueryHandler(reader, new StubCurrentTeam(teamId))
            .Handle(new ListToolCallsQuery { AgentRunId = Guid.NewGuid() }, CancellationToken.None);

        ReferenceEquals(result, rows).ShouldBeTrue("the handler is a pure dispatcher; SQL projection and ordering belong to the audit reader");
    }

    private sealed class CapturingAuditReader : IToolCallAuditReader
    {
        private readonly IReadOnlyList<ToolCallView> _rows;

        public CapturingAuditReader(IReadOnlyList<ToolCallView> rows) { _rows = rows; }

        public Guid LastRunId { get; private set; }
        public Guid LastTeamId { get; private set; }

        public Task<IReadOnlyList<ToolCallView>> ListForRunAsync(Guid agentRunId, Guid teamId, CancellationToken cancellationToken)
        {
            LastRunId = agentRunId;
            LastTeamId = teamId;
            return Task.FromResult(_rows);
        }

        public Task<ToolCallPage?> PageForRunAsync(PageToolCallsQuery request, Guid teamId, CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    private sealed class StubCurrentTeam : ICurrentTeam
    {
        public StubCurrentTeam(Guid? id) { Id = id; }

        public Guid? Id { get; }
        public bool IsSet => Id is not null;
    }
}
