using System.Collections;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Agents;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace CodeSpace.Core.Services.Agents.Parsers.ClaudeCode;

/// <summary>
/// Parses a Claude-Code subagent artifact — markdown with a leading <c>---</c> YAML frontmatter block
/// (<c>name</c>, <c>description</c>, <c>model</c>, <c>tools</c>) followed by the system-prompt body.
///
/// The frontmatter fence split is hand-rolled (trivial + safe); the YAML block is parsed with a real YAML
/// reader (YamlDotNet) and re-serialized VERBATIM into <see cref="ParsedAgentDefinition.RawFrontmatterJson"/>
/// so unknown / nested / list / quoted / multiline keys survive losslessly — a key:value line-splitter would
/// mangle them and break the forward-compat contract. The thin index (name/description/model/tools/body) is a
/// secondary read of known keys. The <c>tools</c> quirk — Claude-Code writes a COMMA-separated scalar
/// (<c>tools: Read, Grep</c>) — is handled here (harness-specific), preserving null (key absent = harness
/// default) vs [] (present-but-empty = no tools).
/// </summary>
public sealed class ClaudeCodeAgentParser : IAgentArtifactParser, ISingletonDependency
{
    public const string ParserKind = "claude-code";

    public string Kind => ParserKind;

    private static readonly IDeserializer YamlReader = new DeserializerBuilder().Build();
    private static readonly ISerializer YamlToJson = new SerializerBuilder().JsonCompatible().Build();

    public ParsedAgentDefinition Parse(string fileText, string sourcePath)
    {
        var (yaml, body) = SplitFrontmatter(fileText ?? "");

        if (yaml is null)
            return new ParsedAgentDefinition { SourcePath = sourcePath, SystemPrompt = body.Trim(), Diagnostics = new[] { "No YAML frontmatter (expected a leading '---' block); cannot read the agent's name." } };

        Dictionary<string, object?> map;
        try
        {
            map = YamlReader.Deserialize<Dictionary<string, object?>>(yaml) ?? new Dictionary<string, object?>();
        }
        catch (YamlException ex)
        {
            return new ParsedAgentDefinition { SourcePath = sourcePath, SystemPrompt = body.Trim(), Diagnostics = new[] { $"Frontmatter is not valid YAML: {ex.Message}" } };
        }

        var name = ReadScalar(map, "name") ?? "";

        var diagnostics = new List<string>();
        if (string.IsNullOrWhiteSpace(name)) diagnostics.Add("Frontmatter is missing the required 'name' field.");

        return new ParsedAgentDefinition
        {
            SourcePath = sourcePath,
            Name = name,
            Description = ReadScalar(map, "description"),
            Model = NullIfBlank(ReadScalar(map, "model")),
            Tools = ReadTools(map),
            SystemPrompt = body.Trim(),
            RawFrontmatterJson = ToJson(map),
            Diagnostics = diagnostics,
        };
    }

    /// <summary>Split a leading <c>---</c>…<c>---</c> fence: returns (yaml-block, body-after) or (null, whole-text) when there's no well-formed frontmatter.</summary>
    private static (string? Yaml, string Body) SplitFrontmatter(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

        if (lines.Length == 0 || lines[0].Trim() != "---") return (null, text);

        var close = -1;
        for (var i = 1; i < lines.Length; i++)
            if (lines[i].Trim() == "---") { close = i; break; }

        if (close == -1) return (null, text);   // unterminated fence → treat as no frontmatter

        var yaml = string.Join('\n', lines[1..close]);
        var body = string.Join('\n', lines[(close + 1)..]);
        return (yaml, body);
    }

    /// <summary>Read a top-level scalar (name/description/model); null when absent, null-valued, or a collection (not a scalar).</summary>
    private static string? ReadScalar(IReadOnlyDictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var v) || v is null) return null;

        if (v is string s) return s;

        return v is IEnumerable ? null : v.ToString();   // numeric/bool scalar → its text; nested map/list → not a scalar
    }

    /// <summary>
    /// Tools tri-state: key absent → null (harness default); present-but-empty (null / blank scalar / empty list)
    /// → [] (no tools); a comma scalar or a YAML list → the trimmed, non-blank entries.
    /// </summary>
    private static IReadOnlyList<string>? ReadTools(IReadOnlyDictionary<string, object?> map)
    {
        if (!map.TryGetValue("tools", out var value)) return null;

        if (value is null) return Array.Empty<string>();

        if (value is string scalar)
            return scalar.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();

        if (value is IEnumerable list)
            return list.Cast<object?>().Select(t => t?.ToString()?.Trim()).Where(t => !string.IsNullOrEmpty(t)).Cast<string>().ToList();

        return Array.Empty<string>();
    }

    /// <summary>Re-serialize the whole parsed frontmatter map to JSON verbatim (handles nested maps / lists / typed scalars).</summary>
    private static string ToJson(Dictionary<string, object?> map) => YamlToJson.Serialize(map).Trim();

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
