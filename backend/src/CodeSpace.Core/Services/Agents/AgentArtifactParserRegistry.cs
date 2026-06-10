using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Default <see cref="IAgentArtifactParserRegistry"/> — indexes every registered <see cref="IAgentArtifactParser"/>
/// by its <see cref="IAgentArtifactParser.Kind"/>. Mirrors <c>AgentHarnessRegistry</c>: DI injects all parsers,
/// this dedups + resolves. Registered automatically via <see cref="ISingletonDependency"/>, so adding a parser
/// needs no wiring here.
/// </summary>
public sealed class AgentArtifactParserRegistry : IAgentArtifactParserRegistry, ISingletonDependency
{
    private readonly IReadOnlyDictionary<string, IAgentArtifactParser> _byKind;

    public AgentArtifactParserRegistry(IEnumerable<IAgentArtifactParser> parsers)
    {
        var list = parsers.ToList();

        var duplicates = list.GroupBy(p => p.Kind).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        if (duplicates.Count > 0)
            throw new InvalidOperationException($"Duplicate IAgentArtifactParser kinds: {string.Join(", ", duplicates)}");

        _byKind = list.ToDictionary(p => p.Kind);
        All = list;
    }

    public IReadOnlyList<IAgentArtifactParser> All { get; }

    public IAgentArtifactParser Resolve(string kind)
    {
        if (!_byKind.TryGetValue(kind, out var parser))
            throw new InvalidOperationException($"No IAgentArtifactParser registered for kind '{kind}'. Ensure the corresponding parser is loaded.");

        return parser;
    }
}
