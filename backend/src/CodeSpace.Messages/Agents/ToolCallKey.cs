using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeSpace.Messages.Agents;

/// <summary>
/// Derives the SERVER-side idempotency key + input hash for a side-effecting MCP tool call — the heart of the
/// exactly-once invariant. The key is <c>{toolKind}:{inputHash}</c> where <c>inputHash</c> is the lower-case-hex
/// SHA-256 of the CANONICALIZED input. It is computed server-side from the tool kind + the model's arguments and
/// is NEVER read from the wire: a model-supplied key is a forgery surface (replay an old success for a new write,
/// or pick distinct keys to defeat dedup), so the wire's <c>IdempotencyKey</c> placeholder stays dead.
///
/// <para>Canonicalization is what makes the hash STABLE: the same logical call arriving with reordered object keys
/// / different whitespace / a different number-token spelling must hash identically, or dedup silently fails and a
/// side effect double-runs. <see cref="Canonicalize"/> recursively sorts object keys (ordinal), drops insignificant
/// whitespace, and normalizes number tokens to their <see cref="double"/> round-trip — a pure transform, unit-pinned
/// across permutations. Lives in Messages (a pure noun-helper, Rule 18.1) so both the handler and the tests pin it.</para>
/// </summary>
public static class ToolCallKey
{
    /// <summary>Compose the at-most-once key: <c>{toolKind}:{inputHash}</c>. Dedup is per (run, tool, canonical-input).</summary>
    public static string For(string toolKind, string inputHash) => $"{toolKind}:{inputHash}";

    /// <summary>Lower-case-hex SHA-256 (64 chars) of the canonicalized input. Stable across key-order / whitespace / number-token differences.</summary>
    public static string InputHash(JsonElement input)
    {
        var canonical = Canonicalize(input);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>
    /// A canonical string form of <paramref name="element"/>: object keys sorted ordinally + serialized recursively,
    /// arrays preserve order (order is semantic for a list), numbers normalized to their double round-trip, strings /
    /// booleans / null emitted verbatim. Insignificant whitespace is never produced. Pure + deterministic.
    /// </summary>
    public static string Canonicalize(JsonElement element)
    {
        var sb = new StringBuilder();

        Write(element, sb);

        return sb.ToString();
    }

    private static void Write(JsonElement element, StringBuilder sb)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object: WriteObject(element, sb); break;
            case JsonValueKind.Array: WriteArray(element, sb); break;
            case JsonValueKind.Number: WriteNumber(element, sb); break;
            case JsonValueKind.String: sb.Append(JsonSerializer.Serialize(element.GetString())); break;
            case JsonValueKind.True: sb.Append("true"); break;
            case JsonValueKind.False: sb.Append("false"); break;
            default: sb.Append("null"); break;   // Null + Undefined → "null" (an absent value is canonically null)
        }
    }

    private static void WriteObject(JsonElement element, StringBuilder sb)
    {
        var properties = element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToList();

        sb.Append('{');

        for (var i = 0; i < properties.Count; i++)
        {
            if (i > 0) sb.Append(',');

            sb.Append(JsonSerializer.Serialize(properties[i].Name)).Append(':');
            Write(properties[i].Value, sb);
        }

        sb.Append('}');
    }

    private static void WriteArray(JsonElement element, StringBuilder sb)
    {
        sb.Append('[');

        var first = true;

        foreach (var item in element.EnumerateArray())
        {
            if (!first) sb.Append(',');
            first = false;

            Write(item, sb);
        }

        sb.Append(']');
    }

    // Normalize the number TOKEN so 1, 1.0, and 1e0 all canonicalize identically (the model may spell the same value
    // differently across calls). The double round-trip ("R") collapses the spellings; an integer that overflows
    // double falls back to its raw decimal text (still stable for a given spelling, and out of range for a tool arg).
    private static void WriteNumber(JsonElement element, StringBuilder sb) =>
        sb.Append(element.TryGetDouble(out var d) ? d.ToString("R", CultureInfo.InvariantCulture) : element.GetRawText());
}
