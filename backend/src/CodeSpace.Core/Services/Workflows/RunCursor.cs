using System.Buffers.Text;
using System.Text;

namespace CodeSpace.Core.Services.Workflows;

/// <summary>
/// Opaque keyset cursor for the team runs index — the (created_date, id) of the LAST row on a page. The next page
/// is "everything ordering strictly after this row" under the index's <c>(created_date DESC, id DESC)</c> order, so
/// pagination is stable under concurrent inserts (no OFFSET drift) and the page boundary is deterministic even when
/// rows share a <c>created_date</c> (the <c>id</c> tiebreaker, compared in Postgres uuid order).
///
/// <para>Wire form is an opaque base64url string the client only ever echoes back — it carries
/// <c>{created_date UTC ticks}:{id}</c>. The client must NOT construct or interpret it; a malformed value is a
/// client error (the service rejects it), never silently treated as page one (which would loop forever).</para>
/// </summary>
public readonly record struct RunCursor(DateTimeOffset CreatedDate, Guid Id)
{
    public string Encode()
    {
        var raw = $"{CreatedDate.UtcTicks}:{Id:N}";
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(raw));
    }

    /// <summary>Decode an opaque cursor. Returns null for null/empty (= first page); throws <see cref="InvalidOperationException"/> (→ 400) for a malformed value.</summary>
    public static RunCursor? Decode(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor)) return null;

        try
        {
            var raw = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor));
            var sep = raw.IndexOf(':');

            if (sep > 0
                && long.TryParse(raw.AsSpan(0, sep), out var ticks)
                && Guid.TryParseExact(raw.AsSpan(sep + 1), "N", out var id))
                return new RunCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
        }
        catch (FormatException) { /* fall through to the throw below */ }

        throw new InvalidOperationException("Invalid runs cursor.");
    }
}
