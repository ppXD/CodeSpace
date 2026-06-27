using System.Collections;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Parsers.ClaudeCode;

/// <summary>
/// Parses a Claude-Code subagent artifact — markdown with a leading <c>---</c> YAML frontmatter block
/// (<c>name</c>, <c>description</c>, <c>model</c>, <c>tools</c>) followed by the system-prompt body.
///
/// The fence split + YAML read are the shared <see cref="Frontmatter"/> helper, which preserves the frontmatter
/// VERBATIM into <see cref="ParsedAgentDefinition.RawFrontmatterJson"/> (lossless forward-compat) AND tolerates
/// real-world frontmatter that isn't strict YAML — a contains-studio agent's <c>description</c> embeds
/// <c>&lt;example&gt;</c> blocks with <c>Context:</c> / <c>user: "…"</c> (colon-space), which strict YAML rejects;
/// the lenient fallback still recovers the agent's name so it isn't silently dropped. The thin index
/// (name/description/model/tools/body) is a secondary read of known keys. The <c>tools</c> quirk — Claude-Code
/// writes a COMMA-separated scalar (<c>tools: Read, Grep</c>) — is handled here (harness-specific), preserving null
/// (key absent = harness default) vs [] (present-but-empty = no tools).
/// </summary>
public sealed class ClaudeCodeAgentParser : IAgentArtifactParser, ISingletonDependency
{
    public const string ParserKind = "claude-code";

    public string Kind => ParserKind;

    public ParsedAgentDefinition Parse(string fileText, string sourcePath)
    {
        var (yaml, body) = Frontmatter.Split(fileText ?? "");

        if (yaml is null)
            return new ParsedAgentDefinition { SourcePath = sourcePath, SystemPrompt = body.Trim(), Diagnostics = new[] { "No YAML frontmatter (expected a leading '---' block); cannot read the agent's name." } };

        var fm = Frontmatter.Parse(yaml);

        var name = Frontmatter.ReadScalar(fm.Map, "name") ?? "";

        var diagnostics = new List<string>(fm.Diagnostics);
        if (string.IsNullOrWhiteSpace(name)) diagnostics.Add("Frontmatter is missing the required 'name' field.");

        return new ParsedAgentDefinition
        {
            SourcePath = sourcePath,
            Name = name,
            Description = Frontmatter.ReadScalar(fm.Map, "description"),
            Model = NullIfBlank(Frontmatter.ReadScalar(fm.Map, "model")),
            Tools = ReadTools(fm.Map),
            Skills = ReadSkills(fm.Map),
            SystemPrompt = body.Trim(),
            RawFrontmatterJson = fm.RawJson,
            Diagnostics = diagnostics,
        };
    }

    /// <summary>The skill handles the agent declares (frontmatter <c>skills</c>) — a comma scalar or a YAML list of trimmed, non-blank entries; empty when the key is absent. Unlike tools there's no null/empty tri-state (a skill is bound or not).</summary>
    private static IReadOnlyList<string> ReadSkills(IReadOnlyDictionary<string, object?> map)
    {
        if (!map.TryGetValue("skills", out var value) || value is null) return Array.Empty<string>();

        if (value is string scalar)
            return scalar.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

        if (value is IEnumerable list)
            return list.Cast<object?>().Select(s => s?.ToString()?.Trim()).Where(s => !string.IsNullOrEmpty(s)).Cast<string>().ToList();

        return Array.Empty<string>();
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

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
