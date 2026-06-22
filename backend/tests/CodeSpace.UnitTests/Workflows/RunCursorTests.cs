using CodeSpace.Core.Services.Workflows;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The opaque keyset cursor codec for the runs index. Pins the round-trip (so a NextCursor handed back as ?cursor=
/// reconstructs the exact (created_date, id) boundary), the first-page sentinel (null/empty → null), and the
/// fail-loud contract for a malformed value (a client must only ever echo a cursor we minted — garbage is a 400,
/// never a silent reset to page one, which would loop forever).
/// </summary>
public class RunCursorTests
{
    [Fact]
    public void Round_trips_created_date_and_id_exactly()
    {
        var original = new RunCursor(new DateTimeOffset(2026, 6, 22, 13, 45, 7, 123, TimeSpan.Zero), Guid.NewGuid());

        var decoded = RunCursor.Decode(original.Encode());

        decoded.ShouldNotBeNull();
        decoded!.Value.CreatedDate.ShouldBe(original.CreatedDate);
        decoded.Value.Id.ShouldBe(original.Id);
    }

    [Fact]
    public void Preserves_the_instant_across_offsets_by_comparing_in_utc()
    {
        // The encode key is UtcTicks, so a cursor minted from a +08:00 timestamp decodes to the same INSTANT —
        // the keyset comparison is on the absolute point in time, never a wall-clock-with-offset mismatch.
        var withOffset = new DateTimeOffset(2026, 6, 22, 21, 45, 7, TimeSpan.FromHours(8));

        var decoded = RunCursor.Decode(new RunCursor(withOffset, Guid.NewGuid()).Encode());

        decoded!.Value.CreatedDate.UtcDateTime.ShouldBe(withOffset.UtcDateTime);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Treats_a_null_or_empty_cursor_as_the_first_page(string? cursor)
    {
        RunCursor.Decode(cursor).ShouldBeNull();
    }

    [Theory]
    [InlineData("not-base64url!!")]      // not decodable base64url
    [InlineData("bm90LWEtY3Vyc29y")]     // valid base64url ("not-a-cursor") but no ':' separator
    [InlineData("Zm9vOmJhcg")]           // base64url of "foo:bar" — has a ':' but non-numeric ticks / non-guid id
    public void Rejects_a_malformed_cursor_as_a_client_error(string cursor)
    {
        Should.Throw<InvalidOperationException>(() => RunCursor.Decode(cursor));
    }
}
