using CodeSpace.Core.Services.Agents.Context.Exceptions;
using CodeSpace.Core.Services.Sessions;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

[Trait("Category", "Unit")]
public class SessionAgentEventCursorTests
{
    [Fact]
    public void Cursor_round_trips_only_under_the_same_scope_and_normalized_query()
    {
        var teamId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var encoded = new SessionAgentEventCursor(51).Encode(teamId, sessionId, " evidence ");

        SessionAgentEventCursor.Decode(encoded, teamId, sessionId, "evidence").ShouldBe(new SessionAgentEventCursor(51));
    }

    [Fact]
    public void Null_is_the_only_first_page_cursor()
    {
        SessionAgentEventCursor.Decode(null, Guid.NewGuid(), Guid.NewGuid(), null).ShouldBeNull();
    }

    [Fact]
    public void Cursor_is_bound_to_team_session_and_query()
    {
        var teamId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var encoded = new SessionAgentEventCursor(7).Encode(teamId, sessionId, "build");

        Should.Throw<AgentContextCursorException>(() => SessionAgentEventCursor.Decode(encoded, Guid.NewGuid(), sessionId, "build"));
        Should.Throw<AgentContextCursorException>(() => SessionAgentEventCursor.Decode(encoded, teamId, Guid.NewGuid(), "build"));
        Should.Throw<AgentContextCursorException>(() => SessionAgentEventCursor.Decode(encoded, teamId, sessionId, "test"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64!")]
    [InlineData("dmVy")]
    public void Malformed_cursor_fails_loud(string cursor)
    {
        Should.Throw<AgentContextCursorException>(() => SessionAgentEventCursor.Decode(cursor, Guid.NewGuid(), Guid.NewGuid(), null));
    }

    [Fact]
    public void Non_positive_and_oversized_cursors_are_rejected()
    {
        var teamId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        var zero = new SessionAgentEventCursor(0).Encode(teamId, sessionId, null);
        Should.Throw<AgentContextCursorException>(() => SessionAgentEventCursor.Decode(zero, teamId, sessionId, null));
        Should.Throw<AgentContextCursorException>(() => SessionAgentEventCursor.Decode(new string('a', 513), teamId, sessionId, null));
    }
}
