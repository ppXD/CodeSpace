namespace CodeSpace.Core.Services.Workflows.Nodes;

public sealed class NodeRegistry : INodeRegistry
{
    private readonly IReadOnlyDictionary<string, INodeRuntime> _byKey;

    public NodeRegistry(IEnumerable<INodeRuntime> nodes)
    {
        var list = nodes.ToList();

        // Duplicate type keys are a programming error — fail fast on startup so the
        // operator sees it in the first log lines, not at first workflow execution.
        var duplicates = list.GroupBy(n => n.TypeKey).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        if (duplicates.Count > 0)
            throw new InvalidOperationException($"Duplicate INodeRuntime TypeKeys: {string.Join(", ", duplicates)}");

        _byKey = list.ToDictionary(n => n.TypeKey);
        All = list;
    }

    public IReadOnlyList<INodeRuntime> All { get; }

    public bool Contains(string typeKey) => _byKey.ContainsKey(typeKey);

    public INodeRuntime Resolve(string typeKey)
    {
        if (!_byKey.TryGetValue(typeKey, out var node))
            throw new InvalidOperationException($"No INodeRuntime registered for TypeKey '{typeKey}'");

        return node;
    }
}
