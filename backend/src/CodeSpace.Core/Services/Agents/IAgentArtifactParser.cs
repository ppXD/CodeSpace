using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Parses one ecosystem's agent artifact format into the harness-neutral <see cref="ParsedAgentDefinition"/>.
/// The ONLY ecosystem-specific step in import — everything downstream (preview, commit, the persona entity)
/// is format-neutral. A new ecosystem (a different frontmatter dialect, AGENTS.md, …) lands as a sibling
/// parser with its own <see cref="Kind"/> (Rule 7 / 18.3 — never by widening this), resolved by the registry.
///
/// Stateless + safe to share: the registry resolves one instance per kind.
/// </summary>
public interface IAgentArtifactParser
{
    /// <summary>Stable ecosystem tag the registry resolves by — e.g. "claude-code".</summary>
    string Kind { get; }

    /// <summary>Parse one artifact's text into the neutral shape. Never throws on malformed input — it returns a value with <see cref="ParsedAgentDefinition.Diagnostics"/> populated so a bad file is previewable, not fatal.</summary>
    ParsedAgentDefinition Parse(string fileText, string sourcePath);
}
