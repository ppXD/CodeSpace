using CodeSpace.Core.Services.Agents.Context.Exceptions;
using CodeSpace.Core.Services.Sessions;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

public class SessionEffectReceiptCursorTests
{
    [Fact]
    public void Round_trip_preserves_keyset_and_exact_scope()
    {
        var teamId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var created = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var encoded = new SessionEffectReceiptCursor(created, id).Encode(teamId, sessionId, " deploy ");

        var decoded = SessionEffectReceiptCursor.Decode(encoded, teamId, sessionId, "deploy");

        decoded.ShouldBe(new SessionEffectReceiptCursor(created, id));
    }

    [Fact]
    public void Cursor_is_bound_to_team_session_and_normalized_query()
    {
        var teamId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var encoded = new SessionEffectReceiptCursor(DateTimeOffset.UtcNow, Guid.NewGuid()).Encode(teamId, sessionId, "deploy");

        Should.Throw<AgentContextCursorException>(() => SessionEffectReceiptCursor.Decode(encoded, Guid.NewGuid(), sessionId, "deploy"));
        Should.Throw<AgentContextCursorException>(() => SessionEffectReceiptCursor.Decode(encoded, teamId, Guid.NewGuid(), "deploy"));
        Should.Throw<AgentContextCursorException>(() => SessionEffectReceiptCursor.Decode(encoded, teamId, sessionId, "merge"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64")]
    [InlineData("dmVy")]
    public void Malformed_cursor_fails_loud(string cursor)
    {
        Should.Throw<AgentContextCursorException>(() => SessionEffectReceiptCursor.Decode(cursor, Guid.NewGuid(), Guid.NewGuid(), null));
    }

    [Fact]
    public void Oversized_cursor_is_rejected_before_decode()
    {
        Should.Throw<AgentContextCursorException>(() => SessionEffectReceiptCursor.Decode(new string('a', 513), Guid.NewGuid(), Guid.NewGuid(), null));
    }
}
