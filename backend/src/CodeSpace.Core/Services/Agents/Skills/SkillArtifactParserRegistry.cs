using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Agents.Skills;

/// <summary>
/// Default <see cref="ISkillArtifactParserRegistry"/> — indexes every registered <see cref="ISkillArtifactParser"/>
/// by its <see cref="ISkillArtifactParser.Kind"/>. Mirrors <c>AgentArtifactParserRegistry</c>: DI injects all
/// parsers, this dedups + resolves. Registered automatically via <see cref="ISingletonDependency"/>, so adding a
/// parser needs no wiring here.
/// </summary>
public sealed class SkillArtifactParserRegistry : ISkillArtifactParserRegistry, ISingletonDependency
{
    private readonly IReadOnlyDictionary<string, ISkillArtifactParser> _byKind;

    public SkillArtifactParserRegistry(IEnumerable<ISkillArtifactParser> parsers)
    {
        var list = parsers.ToList();

        var duplicates = list.GroupBy(p => p.Kind).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        if (duplicates.Count > 0)
            throw new InvalidOperationException($"Duplicate ISkillArtifactParser kinds: {string.Join(", ", duplicates)}");

        _byKind = list.ToDictionary(p => p.Kind);
        All = list;
    }

    public IReadOnlyList<ISkillArtifactParser> All { get; }

    public ISkillArtifactParser Resolve(string kind)
    {
        if (!_byKind.TryGetValue(kind, out var parser))
            throw new InvalidOperationException($"No ISkillArtifactParser registered for kind '{kind}'. Ensure the corresponding parser is loaded.");

        return parser;
    }
}
