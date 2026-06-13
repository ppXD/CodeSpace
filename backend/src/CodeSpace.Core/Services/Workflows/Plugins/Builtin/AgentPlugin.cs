using CodeSpace.Core.Services.Workflows.Nodes.Builtin;

namespace CodeSpace.Core.Services.Workflows.Plugins.Builtin;

/// <summary>
/// The agent-execution nodes — <c>agent.code</c> (runs a coding agent — Codex, Claude Code, … — as a step)
/// and <c>agent.run_command</c> (runs one shell command in a sandbox, optionally inside a cloned repo).
/// Both sit on the agent execution layer (<c>IAgentHarness</c> / <c>ISandboxRunner</c> + the workspace
/// providers). Removing this plugin disables the agent steps cleanly; the engine still runs git / llm /
/// http / logic flows. Auto-discovered by the DI module's <c>IPluginModule</c> scan — adding it needs no
/// engine edits.
/// </summary>
public sealed class AgentPlugin : IPluginModule
{
    public string Name => "Agent";

    public IReadOnlyList<Type> Nodes { get; } = new[] { typeof(AgentCodeNode), typeof(AgentRunCommandNode) };

    public IReadOnlyList<Type> RunSourceMatchers { get; } = Array.Empty<Type>();
    public IReadOnlyList<Type> AuxiliaryServices { get; } = Array.Empty<Type>();
}
