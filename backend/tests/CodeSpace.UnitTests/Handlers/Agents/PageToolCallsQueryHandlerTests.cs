using CodeSpace.Core.Handlers.QueryHandlers.Agents;
using CodeSpace.Core.Services.Agents.Exceptions;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Queries.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Handlers.Agents;

[Trait("Category", "Unit")]
public sealed class PageToolCallsQueryHandlerTests
{
    [Fact]
    public async Task Delegates_the_closed_request_with_the_callers_team_and_returns_the_reader_page()
    {
        var teamId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var request = new PageToolCallsQuery { AgentRunId = runId, Direction = ToolCallPageDirection.Older, Cursor = "opaque", Limit = 25 };
        var expected = new ToolCallPage { AgentRunId = runId, Mode = "Older", RequestCursor = "opaque", Items = [], HasOlder = false };
        var reader = new CapturingReader(expected);

        var actual = await new PageToolCallsQueryHandler(reader, new StubCurrentTeam(teamId)).Handle(request, CancellationToken.None);

        ReferenceEquals(actual, expected).ShouldBeTrue();
        ReferenceEquals(reader.Request, request).ShouldBeTrue();
        reader.TeamId.ShouldBe(teamId);
    }

    [Fact]
    public async Task Rejects_an_invalid_shape_before_the_reader_is_called()
    {
        var reader = new CapturingReader(null);
        var request = new PageToolCallsQuery { AgentRunId = Guid.NewGuid(), Direction = ToolCallPageDirection.Tail, Cursor = "not-valid-for-tail", Limit = 501 };

        var exception = await Should.ThrowAsync<ToolCallPageRequestException>(() => new PageToolCallsQueryHandler(reader, new StubCurrentTeam(Guid.NewGuid())).Handle(request, CancellationToken.None));

        exception.Errors.Count.ShouldBe(2);
        reader.Request.ShouldBeNull();
    }

    private sealed class CapturingReader : IToolCallAuditReader
    {
        private readonly ToolCallPage? _page;

        public CapturingReader(ToolCallPage? page) { _page = page; }

        public PageToolCallsQuery? Request { get; private set; }
        public Guid TeamId { get; private set; }

        public Task<ToolCallPage?> PageForRunAsync(PageToolCallsQuery request, Guid teamId, CancellationToken cancellationToken)
        {
            Request = request;
            TeamId = teamId;
            return Task.FromResult(_page);
        }

        public Task<IReadOnlyList<ToolCallView>> ListForRunAsync(Guid agentRunId, Guid teamId, CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    private sealed class StubCurrentTeam : ICurrentTeam
    {
        public StubCurrentTeam(Guid? id) { Id = id; }

        public Guid? Id { get; }
        public bool IsSet => Id is not null;
    }
}
