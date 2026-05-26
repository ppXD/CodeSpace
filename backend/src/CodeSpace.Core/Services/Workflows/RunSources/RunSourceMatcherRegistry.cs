namespace CodeSpace.Core.Services.Workflows.RunSources;

public sealed class RunSourceMatcherRegistry : IRunSourceMatcherRegistry
{
    private readonly IReadOnlyDictionary<string, IRunSourceMatcher> _byKey;

    public RunSourceMatcherRegistry(IEnumerable<IRunSourceMatcher> matchers)
    {
        var list = matchers.ToList();

        var duplicates = list.GroupBy(m => m.TypeKey).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        if (duplicates.Count > 0)
            throw new InvalidOperationException($"Duplicate IRunSourceMatcher TypeKeys: {string.Join(", ", duplicates)}");

        _byKey = list.ToDictionary(m => m.TypeKey);
        All = list;
    }

    public IReadOnlyList<IRunSourceMatcher> All { get; }

    public IRunSourceMatcher? Get(string typeKey) => _byKey.TryGetValue(typeKey, out var m) ? m : null;
}
