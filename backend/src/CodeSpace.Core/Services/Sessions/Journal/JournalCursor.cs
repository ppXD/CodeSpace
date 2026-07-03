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
}
