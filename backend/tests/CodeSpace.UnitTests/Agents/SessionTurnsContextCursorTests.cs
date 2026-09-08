using CodeSpace.Core.Services.Agents.Context.Exceptions;
using CodeSpace.Core.Services.Agents.Context.Sources;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

[Trait("Category", "Unit")]
public class SessionTurnsContextCursorTests
{
    [Fact]
    public void Cursor_round_trips_the_keyset_only_under_the_same_scope_and_query()
    {
        var teamId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var encoded = new SessionTurnsContextCursor(51, groupId).Encode(teamId, sessionId, " exact evidence ");

        var decoded = SessionTurnsContextCursor.Decode(encoded, teamId, sessionId, " exact evidence ");

        decoded.ShouldBe(new SessionTurnsContextCursor(51, groupId));
    }

    [Fact]
    public void Null_is_the_only_first_page_cursor()
    {
        SessionTurnsContextCursor.Decode(null, Guid.NewGuid(), Guid.NewGuid(), null).ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not-base64!")]
    public void Malformed_cursors_fail_loud(string cursor)
    {
        Should.Throw<AgentContextCursorException>(() => SessionTurnsContextCursor.Decode(cursor, Guid.NewGuid(), Guid.NewGuid(), null));
    }

    [Fact]
    public void Cursor_is_bound_to_team_session_and_query()
    {
        var teamId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var encoded = new SessionTurnsContextCursor(3, Guid.NewGuid()).Encode(teamId, sessionId, "alpha");

        Should.Throw<AgentContextCursorException>(() => SessionTurnsContextCursor.Decode(encoded, Guid.NewGuid(), sessionId, "alpha"));
        Should.Throw<AgentContextCursorException>(() => SessionTurnsContextCursor.Decode(encoded, teamId, Guid.NewGuid(), "alpha"));
        Should.Throw<AgentContextCursorException>(() => SessionTurnsContextCursor.Decode(encoded, teamId, sessionId, "beta"));
    }

    [Fact]
    public void Oversized_cursor_is_rejected_before_decode()
    {
        Should.Throw<AgentContextCursorException>(() => SessionTurnsContextCursor.Decode(new string('a', 513), Guid.NewGuid(), Guid.NewGuid(), null));
    }
}
