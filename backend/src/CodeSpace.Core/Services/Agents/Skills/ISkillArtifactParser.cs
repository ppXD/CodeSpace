using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Skills;

/// <summary>
/// Parses one ecosystem's skill artifact format into the harness-neutral <see cref="ParsedSkillDefinition"/>.
/// The ONLY ecosystem-specific step in skill import — everything downstream (preview, commit, the skill
/// entity) is format-neutral. A new ecosystem (a different SKILL.md dialect) lands as a sibling parser with
/// its own <see cref="Kind"/> (Rule 7 / 18.3 — never by widening this), resolved by the registry. The skill
/// peer of <see cref="IAgentArtifactParser"/> (separate because the parsed shape differs — ISP).
///
/// Stateless + safe to share: the registry resolves one instance per kind.
/// </summary>
public interface ISkillArtifactParser
{
    /// <summary>Stable ecosystem tag the registry resolves by — e.g. "claude-code".</summary>
    string Kind { get; }

    /// <summary>Parse one artifact's text into the neutral shape. Never throws on malformed input — it returns a value with <see cref="ParsedSkillDefinition.Diagnostics"/> populated so a bad file is previewable, not fatal.</summary>
    ParsedSkillDefinition Parse(string fileText, string sourcePath);
}
