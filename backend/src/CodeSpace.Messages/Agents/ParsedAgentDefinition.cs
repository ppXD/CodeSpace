namespace CodeSpace.Messages.Agents;

/// <summary>
/// The harness-neutral result of parsing ONE agent artifact (e.g. a Claude-Code subagent <c>.md</c>) — the
/// thin index projected into first-class fields PLUS the original frontmatter preserved VERBATIM in
/// <see cref="RawFrontmatterJson"/> (the lossless forward-compat contract: unknown / nested / future keys
/// ride along untouched). Pure data: the import preview/commit layer interprets it; the parser never
/// touches the DB. A malformed or incomplete artifact still parses to a value with <see cref="Diagnostics"/>
/// populated, so a bad file surfaces in the preview rather than aborting the whole pack.
/// </summary>
public sealed record ParsedAgentDefinition
{
    /// <summary>Path of the artifact within its source (e.g. "agents/backend-architect.md") — the stable per-file identity used to select for import.</summary>
    public required string SourcePath { get; init; }

    /// <summary>The persona name (frontmatter <c>name</c>). Empty when the artifact has no usable name — see <see cref="Diagnostics"/>; such an item is shown un-importable in the preview.</summary>
    public string Name { get; init; } = "";

    public string? Description { get; init; }

    /// <summary>The persona's instructions — the markdown body after the frontmatter block.</summary>
    public string SystemPrompt { get; init; } = "";

    /// <summary>Model id, or null to let the harness pick its default (frontmatter <c>model</c>).</summary>
    public string? Model { get; init; }

    /// <summary>Tool allow-list (frontmatter <c>tools</c>). Null = key absent = harness default; empty = present-but-empty = no tools; non-empty = exactly these.</summary>
    public IReadOnlyList<string>? Tools { get; init; }

    /// <summary>Skill handles the agent declares it carries (frontmatter <c>skills</c>); empty when the key is absent. Resolved to AgentSkillBinding rows at import — a handle that matches no team skill is skipped.</summary>
    public IReadOnlyList<string> Skills { get; init; } = Array.Empty<string>();

    /// <summary>The ENTIRE parsed frontmatter, re-serialized to JSON verbatim — the lossless source unknown/future keys survive in.</summary>
    public string RawFrontmatterJson { get; init; } = "{}";

    /// <summary>Per-file parse warnings/errors (missing name, invalid YAML, …). Non-empty + missing name = the item is previewable but not importable.</summary>
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}
