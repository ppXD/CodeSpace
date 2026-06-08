namespace CodeSpace.Core.Services.Workflows.Agents;

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

    /// <summary>Model id, valid within the chosen harness's <see cref="IAgentHarness.Models"/> catalog.</summary>
    public required string Model { get; init; }

    /// <summary>Isolated working directory the agent runs in (AgentRunService prepares it). Null → the runner's default.</summary>
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
