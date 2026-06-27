using System.Collections;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace CodeSpace.Core.Services.Agents.Parsers;

/// <summary>
/// Shared Markdown-frontmatter reader for the ecosystem artifact parsers (agent + skill). Splits the leading
/// <c>---</c> fence and parses the YAML block TOLERANTLY: strict YAML first, but on a YAML error it falls back to
/// a line-based <c>key: value</c> read so real-world frontmatter whose value is not strict YAML still yields its
/// top-level scalars. The motivating real case (caught by the contains-studio real-GitHub E2E): an agent's
/// <c>description</c> is a single unquoted line embedding <c>&lt;example&gt;</c> blocks with <c>Context:</c> /
/// <c>user: "…"</c> (colon-space) — invalid strict YAML ("found invalid mapping") that Claude Code itself reads
/// leniently; without the fallback the whole agent is lost (no name → excluded from discovery). Never throws.
/// </summary>
internal static class Frontmatter
{
    private static readonly IDeserializer YamlReader = new DeserializerBuilder().Build();
    private static readonly ISerializer YamlToJson = new SerializerBuilder().JsonCompatible().Build();

    /// <summary>The parsed frontmatter: the key→value map, its verbatim JSON, and any parse diagnostics (non-empty only when the lenient fallback was used).</summary>
    internal readonly record struct Block(IReadOnlyDictionary<string, object?> Map, string RawJson, IReadOnlyList<string> Diagnostics);

    /// <summary>Split a leading <c>---</c>…<c>---</c> fence: returns (yaml-block, body-after) or (null, whole-text) when there's no well-formed frontmatter.</summary>
    internal static (string? Yaml, string Body) Split(string text)
    {
        var lines = (text ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

        if (lines.Length == 0 || lines[0].Trim() != "---") return (null, text ?? "");

        var close = -1;
        for (var i = 1; i < lines.Length; i++)
            if (lines[i].Trim() == "---") { close = i; break; }

        if (close == -1) return (null, text ?? "");   // unterminated fence → treat as no frontmatter

        return (string.Join('\n', lines[1..close]), string.Join('\n', lines[(close + 1)..]));
    }

    /// <summary>Parse a frontmatter YAML block: strict YAML, else a lenient top-level <c>key: value</c> read with a diagnostic. Never throws.</summary>
    internal static Block Parse(string yaml)
    {
        try
        {
            var map = YamlReader.Deserialize<Dictionary<string, object?>>(yaml) ?? new Dictionary<string, object?>();
            return new Block(map, ToJson(map), Array.Empty<string>());
        }
        catch (YamlException ex)
        {
            var map = ParseLenient(yaml);
            return new Block(map, ToJson(map), new[] { $"Frontmatter is not strict YAML ({ex.Message}); read top-level 'key: value' lines leniently." });
        }
    }

    /// <summary>
    /// Lenient top-level read: each <c>key: value</c> line → key = value (value = the rest of the line). A line that
    /// can't begin a top-level key (leading whitespace/list-dash/comment, no colon, or a multi-word "key") is skipped,
    /// so an unquoted multi-line value's continuation lines don't pollute the map. First write wins on a duplicate key.
    /// Captures the single-line scalars (name/description/model/category/color/tools) the known readers need, even when
    /// the block is not strict YAML. Nested structure is necessarily lost on this fallback — acceptable, since the input
    /// was already not strict YAML and the top-level scalars are what discovery + the thin index depend on.
    /// </summary>
    private static Dictionary<string, object?> ParseLenient(string yaml)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var line in yaml.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
        {
            if (line.Length == 0 || line[0] is ' ' or '\t' or '-' or '#') continue;   // continuation / list item / comment

            var colon = line.IndexOf(':');
            if (colon <= 0) continue;

            var key = line[..colon].Trim();
            if (key.Length == 0 || key.Contains(' ')) continue;   // a top-level key is a single token

            var value = line[(colon + 1)..].Trim();
            if (!map.ContainsKey(key)) map[key] = value.Length == 0 ? null : value;
        }

        return map;
    }

    /// <summary>Read a top-level scalar (name/description/model/category); null when absent, null-valued, or a collection (not a scalar).</summary>
    internal static string? ReadScalar(IReadOnlyDictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var v) || v is null) return null;

        if (v is string s) return s;

        return v is IEnumerable ? null : v.ToString();   // numeric/bool scalar → its text; nested map/list → not a scalar
    }

    private static string ToJson(Dictionary<string, object?> map) => YamlToJson.Serialize(map).Trim();
}
