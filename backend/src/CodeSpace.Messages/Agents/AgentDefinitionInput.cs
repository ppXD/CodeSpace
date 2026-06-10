namespace CodeSpace.Messages.Agents;

/// <summary>
/// The authorable surface of an Agent persona, consolidated into one record so the service methods
/// stay within the parameter cap and adding a field later is non-breaking (same convention as
/// <c>AddCredentialInput</c>). This is the AUTHORING contract only: the import slice owns
/// <c>Skills</c> / <c>McpServers</c> / <c>RawFrontmatter</c> / provenance, so those are intentionally
/// absent here and left untouched by create/update.
/// </summary>
public sealed record AgentDefinitionInput
{
    /// <summary>Human-readable name; the @-mention slug is derived from it (operators never type the handle).</summary>
    public required string Name { get; init; }

    /// <summary>Routing/trigger description ("use PROACTIVELY when…"). Optional.</summary>
    public string? Description { get; init; }

    /// <summary>The persona's instructions. Null/omitted = an empty prompt.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Model id, or null to let the chosen harness/CLI pick its own default (the Model=empty rule).</summary>
    public string? Model { get; init; }

    /// <summary>Default autonomy level name (suggest / guarded / autonomous); null → guarded. Free string — the dial parses it.</summary>
    public string? DefaultAutonomy { get; init; }

    /// <summary>Tool allow-list (names/patterns). Null = the harness's default toolset; empty = no tools; non-empty = exactly these.</summary>
    public IReadOnlyList<string>? Tools { get; init; }
}
