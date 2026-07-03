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
}
