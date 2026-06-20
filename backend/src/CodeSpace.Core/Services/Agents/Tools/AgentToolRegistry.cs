using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Workflows.Nodes;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.Tools;

/// <summary>
/// Builds the agent-tool catalog from TWO sources: the TOOL-ELIGIBLE workflow nodes (manifest
/// <see cref="NodeManifest.IsAgentToolEligible"/>) projected through <see cref="NodeAgentTool"/>, AND any
/// FIRST-PARTY <see cref="IAgentTool"/>s registered directly in DI (e.g. <see cref="DecisionRequestTool"/> — an
/// MCP-native ask that is not a workflow node). Mirrors the LLM-client / sandbox-runner registry pattern: collect via
/// DI, key by <see cref="IAgentTool.Kind"/>, fail loudly on a duplicate so two sources can't claim one tool name. The
/// first-party set is gated at registration time (a feature-flagged tool simply isn't in the DI collection), so the
/// catalog is byte-identical to node-only when no first-party tool is registered.
/// </summary>
public sealed class AgentToolRegistry : IAgentToolRegistry, IScopedDependency
{
    private readonly IReadOnlyDictionary<string, IAgentTool> _byKind;

    public AgentToolRegistry(IEnumerable<INodeRuntime> nodes, IEnumerable<IAgentTool> firstPartyTools, ILoggerFactory loggerFactory)
    {
        var nodeTools = nodes
            .Where(n => n.Manifest.IsAgentToolEligible)
            .Select(IAgentTool (n) => new NodeAgentTool(n, loggerFactory.CreateLogger($"AgentTool.{n.TypeKey}")));

        var tools = nodeTools.Concat(firstPartyTools).ToList();

        var duplicates = tools.GroupBy(t => t.Kind).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        if (duplicates.Count > 0)
            throw new InvalidOperationException($"Duplicate agent tool kinds: {string.Join(", ", duplicates)}");

        All = tools.OrderBy(t => t.Kind, StringComparer.Ordinal).ToList();
        _byKind = tools.ToDictionary(t => t.Kind, StringComparer.Ordinal);
    }

    public IReadOnlyList<IAgentTool> All { get; }

    public IAgentTool? Resolve(string kind) => _byKind.GetValueOrDefault(kind);
}
