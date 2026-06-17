using CodeSpace.Messages.Agents;

namespace CodeSpace.Messages.Tasks;

/// <summary>
/// The resolved agent envelope a task projection stamps onto the <c>agent.code</c> step(s) it emits (Rule 18.1,
/// a pure data noun) — repo / harness / model / persona / credential / runner / MCP / autonomy / tools.
/// Mirrors <c>SupervisorAgentProfile</c> (plus <c>AllowedTools</c>, which on the supervisor lives on the parent
/// <c>SupervisorGoalConfig</c>) so a single-agent task and a supervisor-spawned agent project the SAME envelope
/// onto the SAME <c>agent.code</c> config keys. Every field is OPTIONAL and folds to
/// the SAME default <c>agent.code</c> uses: a null harness → the harness default, a null autonomy → Standard,
/// a null repo → analysis-only, a null persona → a pure-inline run.
/// </summary>
public sealed record ResolvedAgentProfile
{
    /// <summary>The repository the agent clones into its workspace (the executor clones it) — the PRIMARY repo of a multi-repo workspace. Null → no workspace (analysis-only).</summary>
    public Guid? RepositoryId { get; init; }

    /// <summary>Multi-repo: the RELATED repositories the agent's workspace also clones (alias + access; the primary is <see cref="RepositoryId"/>). Null / empty → a single-repo workspace (byte-identical). The projection emits these onto the agent.code node's <c>relatedRepositories</c> input, which the node folds into <c>AgentTask.Workspace</c>.</summary>
    public IReadOnlyList<WorkspaceRepositorySpec>? RelatedRepositories { get; init; }

    /// <summary>The harness the agent runs on (e.g. <c>"codex-cli"</c>). Null / blank → the projection's harness default.</summary>
    public string? Harness { get; init; }

    /// <summary>The model id within the harness's catalog. Null / blank → the persona's model → the harness default (the model-empty rule).</summary>
    public string? Model { get; init; }

    /// <summary>The Agent persona (<c>AgentDefinition</c>) the agent embodies. Null → a pure-inline run; when set, the dispatch-time resolver merges its system prompt + model + tools + credential into the task.</summary>
    public Guid? AgentDefinitionId { get; init; }

    /// <summary>The <c>ModelCredential</c> reference the agent authenticates with (decrypted just-in-time). Null → the persona default → the team/operator fallback.</summary>
    public Guid? ModelCredentialId { get; init; }

    /// <summary>The sandbox runner the agent executes on (e.g. <c>"local"</c>). Null → the executor's default.</summary>
    public string? RunnerKind { get; init; }

    /// <summary>The autonomy tier the agent runs at, parsed case-insensitively. Null / unrecognised → the safe <c>Standard</c> default.</summary>
    public string? AutonomyLevel { get; init; }

    /// <summary>Per-run opt-in to the MCP tool-fabric endpoint. Null → defer to the ambient deployment flag (an ordinary run is unchanged).</summary>
    public bool? EnableMcp { get; init; }

    /// <summary>The tool allow-list the agent is restricted to. Null → the harness default; non-empty → exactly these (UNIONed with a persona's tools by the dispatch-time resolver).</summary>
    public IReadOnlyList<string>? AllowedTools { get; init; }
}
