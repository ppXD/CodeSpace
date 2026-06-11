namespace CodeSpace.Messages.Agents;

/// <summary>
/// The substrate-neutral "agent task envelope" — what the workflow hands to the agent layer. It says
/// WHAT to do (goal), with WHICH harness + model, and under what permissions; it never mentions a CLI
/// flag. Each <see cref="IAgentHarness"/> translates this into its own invocation, so swapping harness
/// or runner never touches the Workflow domain.
///
/// B0.2 carries the fields the harness consumes to build an invocation. Repo/branch resolution,
/// expected-outputs, and credential refs are added when AgentRunService (B0.3) owns workspace prep.
/// </summary>
public sealed record AgentTask
{
    /// <summary>Natural-language goal / prompt for the agent.</summary>
    public required string Goal { get; init; }

    /// <summary>Harness kind to run this task — resolved via <see cref="IAgentHarnessRegistry"/> (e.g. "codex-cli").</summary>
    public required string Harness { get; init; }

    /// <summary>Model id within the chosen harness's <see cref="IAgentHarness.Models"/> catalog, or null/blank to let the harness pick its own default (the Model=empty rule).</summary>
    public string? Model { get; init; }

    /// <summary>
    /// Optional Agent persona (<c>AgentDefinition</c>) this run resolves from — null = a pure-inline run (no persona).
    /// When set, the dispatch-time <c>IAgentDefinitionResolver</c> merges the persona's system prompt + model into this
    /// task before the run is persisted; the id is preserved here as run provenance.
    /// </summary>
    public Guid? AgentDefinitionId { get; init; }

    /// <summary>
    /// Optional <c>ModelCredential</c> this run authenticates with — a REFERENCE (id), never the key, so it's safe to
    /// freeze into the persisted task. Resolved by the dispatch-time <c>IAgentDefinitionResolver</c> with the precedence
    /// node-override &gt; persona default &gt; null; the executor decrypts it just-in-time and injects it into the sandbox
    /// env, never persisting the secret. Null = fall back to a team default / operator-global key at resolve time.
    /// </summary>
    public Guid? ModelCredentialId { get; init; }

    /// <summary>
    /// Tool allow-list the harness restricts the agent to — null = the harness's default toolset, empty = no tools,
    /// non-empty = exactly these. A harness that supports allow-lists projects it (Claude Code → <c>--allowed-tools</c>);
    /// one that doesn't (Codex, which restricts via sandbox) carries it without enforcement. Resolved from the persona's
    /// tools UNIONed with any node-level tools by the dispatch-time resolver.
    /// </summary>
    public IReadOnlyList<string>? Tools { get; init; }

    /// <summary>Sandbox runner to execute on — e.g. "local", "docker", "k8s". Null → the executor's default. The knob for choosing / overriding the execution backend per run.</summary>
    public string? RunnerKind { get; init; }

    /// <summary>Bound repository the agent works in — the executor clones it into the workspace before running the harness. Null → no workspace (analysis-only / no-repo run). One workspace source among future others (raw URL, upstream-produced); all resolve to a <c>WorkspaceRequest</c>.</summary>
    public Guid? RepositoryId { get; init; }

    /// <summary>Isolated working directory the agent runs in (the executor prepares it from <see cref="RepositoryId"/>). Null → the runner's default.</summary>
    public string? WorkspaceDirectory { get; init; }

    /// <summary>What the agent is allowed to do — mapped by the harness onto its sandbox flags.</summary>
    public AgentPermissions Permissions { get; init; } = new();

    /// <summary>Extra environment for the agent process (short-lived credentials are injected here by AgentRunService, then wiped).</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>Wall-clock cap for the whole agent run.</summary>
    public int TimeoutSeconds { get; init; } = 1800;
}

/// <summary>What the agent may do — the declarative half of the sandbox policy a harness maps to its flags.</summary>
public sealed record AgentPermissions
{
    public AgentNetworkAccess Network { get; init; } = AgentNetworkAccess.Off;

    public AgentWriteScope WriteScope { get; init; } = AgentWriteScope.Workspace;
}

public enum AgentNetworkAccess
{
    Off,
    On,
}

public enum AgentWriteScope
{
    /// <summary>May only write inside its workspace directory.</summary>
    Workspace,

    /// <summary>May not write at all (analysis-only).</summary>
    ReadOnly,
}
