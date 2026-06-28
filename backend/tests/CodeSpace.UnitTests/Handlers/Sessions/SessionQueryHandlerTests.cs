using CodeSpace.Core.Handlers.QueryHandlers.Sessions;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Sessions;
using CodeSpace.Messages.Dtos.Sessions;
using CodeSpace.Messages.Queries.Sessions;
using Shouldly;

namespace CodeSpace.UnitTests.Handlers.Sessions;

/// <summary>
/// Unit-proves the three session query handlers are thin dispatchers (Rule 16): each scopes to the CALLER'S team
/// (<see cref="ICurrentTeam"/>, never the wire) and forwards the request verbatim to <see cref="ISessionReadService"/>.
/// No DbContext; the scope + forward is the whole behaviour.
/// </summary>
[Trait("Category", "Unit")]
public class SessionQueryHandlerTests
{
    [Fact]
    public async Task List_scopes_to_the_callers_team_and_forwards_cursor_and_limit()
    {
        var teamId = Guid.NewGuid();
        var svc = new CapturingReadService();

        await new ListTeamSessionsQueryHandler(svc, new StubCurrentTeam(teamId))
            .Handle(new ListTeamSessionsQuery { Cursor = "c1", Limit = 25 }, CancellationToken.None);

        svc.ListCall.ShouldNotBeNull();
        svc.ListCall!.Value.teamId.ShouldBe(teamId, "scoped to the caller's team, never the wire");
        svc.ListCall.Value.cursor.ShouldBe("c1");
        svc.ListCall.Value.limit.ShouldBe(25);
    }

    [Fact]
    public async Task Detail_scopes_to_the_callers_team_and_forwards_the_session_id()
    {
        var teamId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var svc = new CapturingReadService();

        await new GetSessionDetailQueryHandler(svc, new StubCurrentTeam(teamId))
            .Handle(new GetSessionDetailQuery { SessionId = sessionId }, CancellationToken.None);

        svc.DetailCall.ShouldBe((sessionId, teamId));
    }

    [Fact]
    public async Task By_run_scopes_to_the_callers_team_and_forwards_the_run_id()
    {
        var teamId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var svc = new CapturingReadService();

        await new GetSessionByRunQueryHandler(svc, new StubCurrentTeam(teamId))
            .Handle(new GetSessionByRunQuery { RunId = runId }, CancellationToken.None);

        svc.ByRunCall.ShouldBe((runId, teamId));
    }

    private sealed class CapturingReadService : ISessionReadService
    {
        public (Guid teamId, string? cursor, int limit)? ListCall { get; private set; }
        public (Guid sessionId, Guid teamId)? DetailCall { get; private set; }
        public (Guid runId, Guid teamId)? ByRunCall { get; private set; }

        public Task<SessionPage> ListAsync(Guid teamId, string? cursor, int limit, CancellationToken cancellationToken)
        {
            ListCall = (teamId, cursor, limit);
            return Task.FromResult(new SessionPage { Items = Array.Empty<SessionSummary>() });
        }

        public Task<SessionDetail?> GetDetailAsync(Guid sessionId, Guid teamId, CancellationToken cancellationToken)
        {
            DetailCall = (sessionId, teamId);
            return Task.FromResult<SessionDetail?>(null);
        }

        public Task<SessionDetail?> GetByRunAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
        {
            ByRunCall = (runId, teamId);
            return Task.FromResult<SessionDetail?>(null);
        }
    }

    private sealed class StubCurrentTeam : ICurrentTeam
    {
        public StubCurrentTeam(Guid? id) { Id = id; }

        public Guid? Id { get; }
        public bool IsSet => Id is not null;
    }
}
