using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Workflows.Nodes;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.Tools;

/// <summary>
/// Builds the agent-tool catalog by projecting the TOOL-ELIGIBLE workflow nodes (manifest
/// <see cref="NodeManifest.IsAgentToolEligible"/>) through <see cref="NodeAgentTool"/>. Mirrors the
/// LLM-client / sandbox-runner registry pattern: collect the registered <see cref="INodeRuntime"/>s via DI,
/// filter, key by <see cref="IAgentTool.Kind"/>, fail loudly on a duplicate so two nodes can't claim one tool name.
/// </summary>
public sealed class AgentToolRegistry : IAgentToolRegistry, IScopedDependency
{
    private readonly IReadOnlyDictionary<string, IAgentTool> _byKind;

    public AgentToolRegistry(IEnumerable<INodeRuntime> nodes, ILoggerFactory loggerFactory)
    {
        var tools = nodes
            .Where(n => n.Manifest.IsAgentToolEligible)
            .Select(IAgentTool (n) => new NodeAgentTool(n, loggerFactory.CreateLogger($"AgentTool.{n.TypeKey}")))
            .ToList();

        var duplicates = tools.GroupBy(t => t.Kind).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        if (duplicates.Count > 0)
            throw new InvalidOperationException($"Duplicate agent tool kinds: {string.Join(", ", duplicates)}");

        All = tools.OrderBy(t => t.Kind, StringComparer.Ordinal).ToList();
        _byKind = tools.ToDictionary(t => t.Kind, StringComparer.Ordinal);
    }

    public IReadOnlyList<IAgentTool> All { get; }

    public IAgentTool? Resolve(string kind) => _byKind.GetValueOrDefault(kind);
}
