using System.Text;
using CodeSpace.Core.Services.Sessions.Journal;
using CodeSpace.Messages.Tasks.Timeline;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions.Journal;

/// <summary>
/// 🟢 Unit: the opaque journal cursor. Pins that it encodes the event's SORT KEY (OccurredAt · SourceKey · Order · Id) —
/// so it is deterministic (same event → same cursor, the stability the delta relies on) and DISTINGUISHES any two events
/// that differ in that key (incl. two events at the same instant, via the id tie-break). Base64 → an opaque, wire-safe token.
/// </summary>
[Trait("Category", "Unit")]
public class JournalCursorTests
{
    private static readonly DateTimeOffset T = new(2026, 7, 3, 12, 0, 0, TimeSpan.Zero);

    private static RunTimelineEvent Event(string id, string sourceKey = "supervisor", long order = 0, DateTimeOffset? at = null) => new()
    {
        Id = id, Kind = "k", Title = "t", Severity = TimelineSeverity.Info, Level = TimelineLevel.Detail,
        OccurredAt = at ?? T, Order = order, SourceKey = sourceKey,
    };

    [Fact]
    public void Is_deterministic_for_the_same_event()
    {
        var e = Event("supervisor-1");

        JournalCursor.Encode(e).ShouldBe(JournalCursor.Encode(e), "same event → same cursor — the stability a ?since= delta relies on");
    }

    [Fact]
    public void Distinguishes_events_that_differ_in_any_sort_key_field()
    {
        var baseCursor = JournalCursor.Encode(Event("a"));

        JournalCursor.Encode(Event("b")).ShouldNotBe(baseCursor, "a different id → a different cursor");
        JournalCursor.Encode(Event("a", sourceKey: "tool-calls")).ShouldNotBe(baseCursor, "a different source → a different cursor");
        JournalCursor.Encode(Event("a", order: 7)).ShouldNotBe(baseCursor, "a different order → a different cursor");
        JournalCursor.Encode(Event("a", at: T.AddTicks(1))).ShouldNotBe(baseCursor, "a different instant → a different cursor");
    }

    [Fact]
    public void Two_events_at_the_same_instant_still_get_distinct_cursors()
    {
        // The id tie-break is in the key, so a merged same-tick pair never collides on the cursor (the FE keys on it).
        JournalCursor.Encode(Event("a", at: T)).ShouldNotBe(JournalCursor.Encode(Event("b", at: T)));
    }

    [Fact]
    public void The_token_is_valid_base64_carrying_the_sort_key()
    {
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(JournalCursor.Encode(Event("supervisor-1", sourceKey: "supervisor", order: 3))));

        decoded.ShouldContain(T.UtcTicks.ToString(), Case.Sensitive, "the instant is encoded as UTC ticks");
        decoded.ShouldContain("supervisor", Case.Sensitive);
        decoded.ShouldContain("supervisor-1", Case.Sensitive);
        decoded.ShouldContain("3", Case.Sensitive);
    }

    // ── Decode + Compare (the ?since= delta) ─────────────────────────────────────────

    [Fact]
    public void Decode_round_trips_encode()
    {
        var decoded = JournalCursor.Decode(JournalCursor.Encode(Event("supervisor-1", sourceKey: "supervisor", order: 3, at: T)));

        decoded.ShouldNotBeNull();
        decoded!.Value.Ticks.ShouldBe(T.UtcTicks);
        decoded.Value.SourceKey.ShouldBe("supervisor");
        decoded.Value.Order.ShouldBe(3);
        decoded.Value.Id.ShouldBe("supervisor-1");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not-base64!!")]
    [InlineData("YWJj")]          // valid base64 but not the 4-field shape ("abc")
    public void Decode_rejects_a_malformed_cursor(string? cursor)
    {
        JournalCursor.Decode(cursor).ShouldBeNull("an old / forged / truncated token is not a cursor");
    }

    [Fact]
    public void Compare_orders_by_the_projector_sort_key()
    {
        // Later instant is AFTER; at one instant the tie-break runs SourceKey → Order → Id, matching the timeline merge.
        JournalCursor.Compare(JournalCursor.Encode(Event("a", at: T.AddTicks(1))), JournalCursor.Encode(Event("a", at: T))).ShouldBeGreaterThan(0, "a later instant sorts after");
        JournalCursor.Compare(JournalCursor.Encode(Event("a", sourceKey: "tool-calls")), JournalCursor.Encode(Event("a", sourceKey: "supervisor"))).ShouldBeGreaterThan(0, "at one instant, source key breaks the tie (ordinal)");
        JournalCursor.Compare(JournalCursor.Encode(Event("a", order: 5)), JournalCursor.Encode(Event("a", order: 2))).ShouldBeGreaterThan(0, "then order");
        JournalCursor.Compare(JournalCursor.Encode(Event("b")), JournalCursor.Encode(Event("a"))).ShouldBeGreaterThan(0, "then id");
    }

    [Fact]
    public void Compare_of_equal_cursors_is_zero()
    {
        var c = JournalCursor.Encode(Event("supervisor-1"));

        JournalCursor.Compare(c, c).ShouldBe(0);
    }

    [Fact]
    public void Compare_falls_back_to_ordinal_on_a_malformed_cursor_without_throwing()
    {
        Should.NotThrow(() => JournalCursor.Compare("garbage", JournalCursor.Encode(Event("a"))));
    }

    [Fact]
    public void Compare_agrees_with_the_timeline_projectors_merge_order()
    {
        // THE delta-soundness invariant: ordering cursors by Compare must equal ordering the events the way the timeline
        // projector's Merge does (OccurredAt → SourceKey ordinal → Order → Id ordinal). If they diverge, a ?since delta
        // skips or double-serves steps. Span every tie-break dimension in a deliberately-shuffled input.
        var events = new[]
        {
            Event("z", sourceKey: "supervisor", order: 0, at: T.AddSeconds(2)),
            Event("a", sourceKey: "tool-calls", order: 0, at: T),           // same tick as the next two — source breaks it
            Event("a", sourceKey: "supervisor", order: 5, at: T),
            Event("a", sourceKey: "supervisor", order: 2, at: T),           // same tick+source as above — order breaks it
            Event("b", sourceKey: "agent-events", order: 0, at: T),
            Event("a", sourceKey: "agent-events", order: 0, at: T),         // same tick+source+order as above — id breaks it
        };

        var byCursor = events.OrderBy(e => JournalCursor.Encode(e), Comparer<string>.Create(JournalCursor.Compare)).Select(e => (e.SourceKey, e.Order, e.Id, e.OccurredAt)).ToList();

        // The reference: the projector's exact sort key.
        var byProjector = events
            .OrderBy(e => e.OccurredAt).ThenBy(e => e.SourceKey, StringComparer.Ordinal).ThenBy(e => e.Order).ThenBy(e => e.Id, StringComparer.Ordinal)
            .Select(e => (e.SourceKey, e.Order, e.Id, e.OccurredAt)).ToList();

        byCursor.ShouldBe(byProjector, "Compare orders cursors exactly as the timeline projector merges the events");
    }
}
