using System.Collections;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Agents;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace CodeSpace.Core.Services.Agents.Skills.Parsers.ClaudeCode;

/// <summary>
/// Parses a Claude-Code / Agent-Skills <c>SKILL.md</c> artifact — markdown with a leading <c>---</c> YAML
/// frontmatter block (<c>name</c>, <c>description</c>, optional <c>category</c>) followed by the instruction
/// body. The skill peer of <c>ClaudeCodeAgentParser</c>; self-contained with its own frontmatter helpers
/// (same convention — each parser owns its split + read), so the agent parser is untouched.
///
/// The frontmatter fence split is hand-rolled (trivial + safe); the YAML block is parsed with a real YAML
/// reader (YamlDotNet) and re-serialized VERBATIM into <see cref="ParsedSkillDefinition.RawFrontmatterJson"/>
/// so unknown / nested / future keys survive losslessly. <c>name</c> + <c>description</c> are the two
/// load-bearing fields (the open Agent-Skills spec's required pair); a missing one is a diagnostic, not a
/// throw — the file stays previewable.
/// </summary>
public sealed class ClaudeCodeSkillParser : ISkillArtifactParser, ISingletonDependency
{
    public const string ParserKind = "claude-code";

    public string Kind => ParserKind;

    private static readonly IDeserializer YamlReader = new DeserializerBuilder().Build();
    private static readonly ISerializer YamlToJson = new SerializerBuilder().JsonCompatible().Build();

    public ParsedSkillDefinition Parse(string fileText, string sourcePath)
    {
        var (yaml, body) = SplitFrontmatter(fileText ?? "");

        if (yaml is null)
            return new ParsedSkillDefinition { SourcePath = sourcePath, Body = body.Trim(), Diagnostics = new[] { "No YAML frontmatter (expected a leading '---' block); cannot read the skill's name." } };

        Dictionary<string, object?> map;
        try
        {
            map = YamlReader.Deserialize<Dictionary<string, object?>>(yaml) ?? new Dictionary<string, object?>();
        }
        catch (YamlException ex)
        {
            return new ParsedSkillDefinition { SourcePath = sourcePath, Body = body.Trim(), Diagnostics = new[] { $"Frontmatter is not valid YAML: {ex.Message}" } };
        }

        var name = ReadScalar(map, "name") ?? "";
        var description = ReadScalar(map, "description");

        var diagnostics = new List<string>();
        if (string.IsNullOrWhiteSpace(name)) diagnostics.Add("Frontmatter is missing the required 'name' field.");
        if (string.IsNullOrWhiteSpace(description)) diagnostics.Add("Frontmatter is missing the required 'description' field (the skill's trigger).");

        return new ParsedSkillDefinition
        {
            SourcePath = sourcePath,
            Name = name,
            Description = description,
            Category = NullIfBlank(ReadScalar(map, "category")),
            Body = body.Trim(),
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

    /// <summary>Read a top-level scalar (name/description/category); null when absent, null-valued, or a collection (not a scalar).</summary>
    private static string? ReadScalar(IReadOnlyDictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var v) || v is null) return null;

        if (v is string s) return s;

        return v is IEnumerable ? null : v.ToString();   // numeric/bool scalar → its text; nested map/list → not a scalar
    }

    /// <summary>Re-serialize the whole parsed frontmatter map to JSON verbatim (handles nested maps / lists / typed scalars).</summary>
    private static string ToJson(Dictionary<string, object?> map) => YamlToJson.Serialize(map).Trim();

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
