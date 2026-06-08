using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Default <see cref="IAgentHarnessRegistry"/> — indexes every registered <see cref="IAgentHarness"/>
/// by its <see cref="IAgentHarness.Kind"/>. Mirrors <c>SandboxRunnerRegistry</c>: DI injects all
/// harnesses, this dedups + resolves. Registered automatically via the <see cref="ISingletonDependency"/>
/// marker, so adding a harness needs no wiring here.
/// </summary>
public sealed class AgentHarnessRegistry : IAgentHarnessRegistry, ISingletonDependency
{
    private readonly IReadOnlyDictionary<string, IAgentHarness> _byKind;

    public AgentHarnessRegistry(IEnumerable<IAgentHarness> harnesses)
    {
        var list = harnesses.ToList();

        var duplicates = list.GroupBy(h => h.Kind).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        if (duplicates.Count > 0)
            throw new InvalidOperationException($"Duplicate IAgentHarness kinds: {string.Join(", ", duplicates)}");

        _byKind = list.ToDictionary(h => h.Kind);
        All = list;
    }

    public IReadOnlyList<IAgentHarness> All { get; }

    public IAgentHarness Resolve(string kind)
    {
        if (!_byKind.TryGetValue(kind, out var harness))
            throw new InvalidOperationException($"No IAgentHarness registered for kind '{kind}'. Ensure the corresponding harness adapter is loaded.");

        return harness;
    }
}
