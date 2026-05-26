namespace CodeSpace.Core.Services.Workflows.Llm;

public sealed class LLMClientRegistry : ILLMClientRegistry
{
    private readonly IReadOnlyDictionary<string, ILLMClient> _byProvider;

    public LLMClientRegistry(IEnumerable<ILLMClient> clients)
    {
        var list = clients.ToList();

        var duplicates = list.GroupBy(c => c.Provider).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        if (duplicates.Count > 0)
            throw new InvalidOperationException($"Duplicate ILLMClient providers: {string.Join(", ", duplicates)}");

        _byProvider = list.ToDictionary(c => c.Provider);
        All = list;
    }

    public IReadOnlyList<ILLMClient> All { get; }

    public ILLMClient Resolve(string provider)
    {
        if (!_byProvider.TryGetValue(provider, out var client))
            throw new InvalidOperationException($"No ILLMClient registered for provider '{provider}'. Ensure the corresponding LLM provider module is loaded.");

        return client;
    }
}
