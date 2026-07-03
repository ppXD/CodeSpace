using System.Text;
using CodeSpace.Messages.Tasks.Timeline;

namespace CodeSpace.Core.Services.Sessions.Journal;

/// <summary>
/// The OPAQUE journal cursor — a stable, per-event delta anchor. <see cref="Encode"/> serializes a timeline event's
/// SORT KEY (the exact tuple the timeline projector orders by: <c>OccurredAt</c> → <c>SourceKey</c> → <c>Order</c> →
/// <c>Id</c>) into a base64 token, so a step's cursor is a function of the EVENT, never of its walk position. That makes
/// it STABLE across re-walks: an earlier event backfilling mid-timeline shifts no existing step's cursor (a positional
/// counter would silently renumber them + break a <c>?since=</c> delta). Pure + deterministic → unit-pinned. The
/// frontend treats the token as opaque (echoes it back verbatim); only the server decodes it (the delta slice adds Decode).
/// </summary>
public static class JournalCursor
{
    /// <summary>The field separator — the ASCII Unit Separator (0x1F), a control char that never appears in a source key / id, so the encoded key round-trips unambiguously.</summary>
    private const char Sep = '\u001f';

    /// <summary>
    /// Encode the event's sort key into an opaque cursor. <c>OccurredAt</c> is stamped as UTC ticks (the instant the
    /// projector compares) and <c>Order</c> as-is; the token is base64 so the wire is opaque + safe. Deterministic: the
    /// SAME event yields the SAME cursor regardless of what else is in the timeline.
    /// </summary>
    public static string Encode(RunTimelineEvent e)
    {
        var canonical = string.Concat(e.OccurredAt.UtcTicks.ToString(), Sep, e.SourceKey, Sep, e.Order.ToString(), Sep, e.Id);

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(canonical));
    }

    /// <summary>Decode a cursor back to its sort-key tuple, or null when it isn't a well-formed journal cursor (an old / forged / truncated token — the caller then treats it as "no cursor" rather than trusting it).</summary>
    public static (long Ticks, string SourceKey, long Order, string Id)? Decode(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor)) return null;

        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split(Sep);

            return parts.Length == 4 && long.TryParse(parts[0], out var ticks) && long.TryParse(parts[2], out var order)
                ? (ticks, parts[1], order, parts[3])
                : null;
        }
        catch (FormatException)
        {
            return null;   // not valid base64 → not a cursor
        }
    }

    /// <summary>
    /// Order two cursors by the SAME key the timeline projector merges on (ticks → source key ordinal → order → id
    /// ordinal), so <c>Compare(step, since) &gt; 0</c> means "the step is strictly AFTER the client's last-seen cursor"
    /// — the delta predicate. A malformed cursor falls back to a raw ordinal compare (deterministic, never throws).
    /// </summary>
    public static int Compare(string a, string b)
    {
        if (Decode(a) is not { } da || Decode(b) is not { } db) return string.CompareOrdinal(a, b);

        var c = da.Ticks.CompareTo(db.Ticks);
        if (c != 0) return c;

        c = string.CompareOrdinal(da.SourceKey, db.SourceKey);
        if (c != 0) return c;

        c = da.Order.CompareTo(db.Order);
        if (c != 0) return c;

        return string.CompareOrdinal(da.Id, db.Id);
    }
}
