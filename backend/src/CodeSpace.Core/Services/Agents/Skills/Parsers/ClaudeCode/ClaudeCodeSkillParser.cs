using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.Parsers;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Skills.Parsers.ClaudeCode;

/// <summary>
/// Parses a Claude-Code / Agent-Skills <c>SKILL.md</c> artifact — markdown with a leading <c>---</c> YAML
/// frontmatter block (<c>name</c>, <c>description</c>, optional <c>category</c>) followed by the instruction body.
/// The skill peer of <c>ClaudeCodeAgentParser</c>; both share the <see cref="Frontmatter"/> reader, which preserves
/// the frontmatter verbatim into <see cref="ParsedSkillDefinition.RawFrontmatterJson"/> and tolerates real-world
/// frontmatter that isn't strict YAML (the lenient fallback still recovers the top-level scalars). <c>name</c> +
/// <c>description</c> are the two load-bearing fields (the open Agent-Skills spec's required pair); a missing one is
/// a diagnostic, not a throw — the file stays previewable.
/// </summary>
public sealed class ClaudeCodeSkillParser : ISkillArtifactParser, ISingletonDependency
{
    public const string ParserKind = "claude-code";

    public string Kind => ParserKind;

    public ParsedSkillDefinition Parse(string fileText, string sourcePath)
    {
        var (yaml, body) = Frontmatter.Split(fileText ?? "");

        if (yaml is null)
            return new ParsedSkillDefinition { SourcePath = sourcePath, Body = body.Trim(), Diagnostics = new[] { "No YAML frontmatter (expected a leading '---' block); cannot read the skill's name." } };

        var fm = Frontmatter.Parse(yaml);

        var name = Frontmatter.ReadScalar(fm.Map, "name") ?? "";
        var description = Frontmatter.ReadScalar(fm.Map, "description");

        var diagnostics = new List<string>(fm.Diagnostics);
        if (string.IsNullOrWhiteSpace(name)) diagnostics.Add("Frontmatter is missing the required 'name' field.");
        if (string.IsNullOrWhiteSpace(description)) diagnostics.Add("Frontmatter is missing the required 'description' field (the skill's trigger).");

        return new ParsedSkillDefinition
        {
            SourcePath = sourcePath,
            Name = name,
            Description = description,
            Category = NullIfBlank(Frontmatter.ReadScalar(fm.Map, "category")),
            Body = body.Trim(),
            RawFrontmatterJson = fm.RawJson,
            Diagnostics = diagnostics,
        };
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
