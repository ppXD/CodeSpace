using System.Text;

namespace CodeSpace.Core.Persistence;

/// <summary>
/// Makes harness/model-produced text storable in Postgres.
///
/// <para>Postgres <c>text</c> and <c>jsonb</c> both refuse U+0000 — there is no encoding of it either type can
/// hold, so the write fails with <c>22021 invalid byte sequence for encoding "UTF8": 0x00</c> and takes the whole
/// transaction with it. Every other control character is accepted verbatim. A CLI harness, a subprocess pipe, or
/// a model completion can put a stray NUL in a line at any time, and when it does the agent run dies for a reason
/// that has nothing to do with the work it was doing: run 33755336097 published no branch because one event's
/// text carried one.</para>
///
/// <para>The two rejections are NOT one problem. A <c>text</c> column rejects the raw BYTE. A <c>jsonb</c> column
/// rejects the six-character ESCAPE that a JSON writer emits for that byte (backslash-u-0000, in a value OR in a
/// key) — a document carrying it holds no NUL byte at all, so removing NUL characters leaves it untouched and
/// Postgres still refuses it. That is why the JSON seam has its own entry point rather than sharing the plain
/// one. Verified against real Postgres 17 in <c>AgentRunNulBytePersistenceTests</c>; other control escapes
/// (backslash-u-0001 and up) are accepted by <c>jsonb</c> and are deliberately left alone.</para>
///
/// <para>The NUL is REMOVED, not replaced: it renders as nothing, carries no meaning a reader could act on, and
/// substituting a visible marker would edit the agent's words. Nothing else about the text is touched — no
/// trimming, no normalization, no re-encoding — and text with nothing to remove is returned as the SAME instance,
/// because the clean case is every event of every run.</para>
/// </summary>
public static class PersistedText
{
    /// <summary>The six characters a JSON writer emits for U+0000. Only <c>jsonb</c> rejects this shape.</summary>
    private const string JsonNulEscape = "\\u0000";

    /// <summary>Strip U+0000 from text bound for a <c>text</c>/<c>varchar</c> column. Null in, null out.</summary>
    public static string? Sanitize(string? value) => value is null || !value.Contains('\0') ? value : value.Replace("\0", string.Empty);

    /// <summary>
    /// Strip both shapes a NUL takes in a JSON document bound for a <c>jsonb</c> column: the raw byte, and the
    /// escape a JSON writer produced for it. Null in, null out. The result is still valid JSON — removing a whole
    /// escape leaves the scanner in the state it was already in.
    /// </summary>
    public static string? SanitizeJson(string? json) => Sanitize(json) is { } value ? RemoveNulEscapes(value) : null;

    /// <summary>
    /// Removes every GENUINE backslash-u-0000 escape, leaving an escaped backslash that merely happens to be
    /// followed by the literal characters <c>u0000</c> intact — that document holds no NUL and <c>jsonb</c> takes
    /// it, while a blind substring removal would eat the second backslash and leave a dangling one, turning valid
    /// JSON into a parse error. An escape is genuine when the backslash introducing it is not itself escaped, i.e.
    /// when an EVEN number of backslashes precede it. Backslashes are legal only inside JSON strings, so no
    /// string-context tracking is needed.
    /// </summary>
    private static string RemoveNulEscapes(string value)
    {
        var match = value.IndexOf(JsonNulEscape, StringComparison.Ordinal);

        if (match < 0) return value;

        var builder = new StringBuilder(value.Length);
        var copied = 0;

        while (match >= 0)
        {
            if (!IsUnescapedBackslashAt(value, match))
            {
                match = value.IndexOf(JsonNulEscape, match + 1, StringComparison.Ordinal);
                continue;
            }

            builder.Append(value, copied, match - copied);
            copied = match + JsonNulEscape.Length;
            match = value.IndexOf(JsonNulEscape, copied, StringComparison.Ordinal);
        }

        if (copied == 0) return value;

        builder.Append(value, copied, value.Length - copied);

        return builder.ToString();
    }

    /// <summary>True when the backslash at <paramref name="index"/> introduces an escape rather than being the escaped half of a preceding pair.</summary>
    private static bool IsUnescapedBackslashAt(string value, int index)
    {
        var preceding = 0;

        while (index - preceding - 1 >= 0 && value[index - preceding - 1] == '\\') preceding++;

        return preceding % 2 == 0;
    }
}
