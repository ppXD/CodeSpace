using CodeSpace.Core.Services.Workflows.Nodes.Builtin;

namespace CodeSpace.Core.Services.Workflows.Plugins.Builtin;

/// <summary>
/// The <c>agent.code</c> node — runs a coding agent (Codex, Claude Code, …) as a workflow step on the
/// agent execution layer (<c>IAgentHarness</c> + <c>ISandboxRunner</c>). Removing this plugin disables
/// the agent step cleanly; the engine still runs git / llm / http / logic flows. Auto-discovered by the
/// DI module's <c>IPluginModule</c> scan — adding it needs no engine edits.
/// </summary>
public sealed class AgentPlugin : IPluginModule
{
    public string Name => "Agent";

    public IReadOnlyList<Type> Nodes { get; } = new[] { typeof(AgentCodeNode) };

    public IReadOnlyList<Type> RunSourceMatchers { get; } = Array.Empty<Type>();
    public IReadOnlyList<Type> AuxiliaryServices { get; } = Array.Empty<Type>();
}
