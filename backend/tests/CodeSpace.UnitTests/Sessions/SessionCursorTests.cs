using CodeSpace.Core.Services.Sessions;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions;

/// <summary>
/// Unit-pins the sessions keyset cursor: it round-trips the (last_activity_at, id) tuple opaquely, treats null/empty as
/// "first page", and REJECTS a malformed value rather than silently restarting from page one (which would loop forever).
/// </summary>
[Trait("Category", "Unit")]
public class SessionCursorTests
{
    [Fact]
    public void Round_trips_the_activity_instant_and_id_through_the_opaque_token()
    {
        var cursor = new SessionCursor(new DateTimeOffset(2026, 6, 28, 12, 34, 56, TimeSpan.Zero), Guid.NewGuid());

        var decoded = SessionCursor.Decode(cursor.Encode());

        decoded.ShouldNotBeNull();
        decoded!.Value.LastActivityAt.ShouldBe(cursor.LastActivityAt);
        decoded.Value.Id.ShouldBe(cursor.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Null_or_empty_decodes_to_the_first_page(string? raw)
    {
        SessionCursor.Decode(raw).ShouldBeNull();
    }

    [Fact]
    public void A_malformed_cursor_throws_rather_than_silently_paging_from_the_start()
    {
        // "Zm9v" is valid base64url ("foo") but has no ':' separator → not a cursor. Must throw, not be treated as page one.
        Should.Throw<InvalidOperationException>(() => SessionCursor.Decode("Zm9v"));
    }
}
