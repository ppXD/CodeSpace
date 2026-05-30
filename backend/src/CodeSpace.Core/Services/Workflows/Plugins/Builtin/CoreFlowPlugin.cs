using CodeSpace.Core.Services.Workflows.Nodes.Builtin;

namespace CodeSpace.Core.Services.Workflows.Plugins.Builtin;

/// <summary>
/// The engine's bedrock plugin: nodes that EVERY workflow needs regardless of domain —
/// the universal Terminal, the manual Start trigger, the branching primitives (logic.if /
/// logic.merge), and the iteration primitive (flow.iterate). These can't be removed without
/// crippling the engine itself, so they ship in their own module that's always loaded.
///
/// Why split from the other domain plugins: separates "what makes the engine work" from
/// "what makes the engine useful for git/llm/http". A future "headless engine for testing"
/// deployment could load ONLY this plugin and still run validation + simple flows. The manual
/// trigger lives here (not in a git/event plugin) because starting a run by hand is a
/// domain-agnostic capability — every deployment has it, with or without webhooks.
/// </summary>
public sealed class CoreFlowPlugin : IPluginModule
{
    public string Name => "Core Flow";

    public IReadOnlyList<Type> Nodes { get; } = new[]
    {
        typeof(TriggerManualNode),
        typeof(TerminalNode),
        typeof(LogicIfNode),
        typeof(LogicMergeNode),
        typeof(FlowIterateNode),
        typeof(FlowSleepNode),
        typeof(FlowWaitApprovalNode),
        typeof(FlowWaitCallbackNode),
    };

    public IReadOnlyList<Type> RunSourceMatchers { get; } = Array.Empty<Type>();
    public IReadOnlyList<Type> AuxiliaryServices { get; } = Array.Empty<Type>();
}
