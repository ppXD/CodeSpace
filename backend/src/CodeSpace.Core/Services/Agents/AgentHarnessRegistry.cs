using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.Harnesses;

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

    public AgentHarnessRegistry(IEnumerable<IAgentHarness> harnesses) : this(harnesses, AgentHarnessDefaults.ConfiguredOverride) { }

    /// <summary>Build over <paramref name="harnesses"/> with the operator's default-harness override handed in rather than read from the process environment — which a test cannot set without every test class running beside it reading the same value.</summary>
    internal AgentHarnessRegistry(IEnumerable<IAgentHarness> harnesses, string? configuredDefault)
    {
        var list = harnesses.ToList();

        var duplicates = list.GroupBy(h => h.Kind).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        if (duplicates.Count > 0)
            throw new InvalidOperationException($"Duplicate IAgentHarness kinds: {string.Join(", ", duplicates)}");

        _byKind = list.ToDictionary(h => h.Kind);
        All = list;

        // Fail-fast on a misconfigured operator default-harness override (CODESPACE_DEFAULT_HARNESS): a deliberate global
        // config typo surfaces at startup, not as a per-run "no harness registered" failure on the unclamped default
        // paths. (A model-hallucinated per-subtask harness is clamped gracefully by the planner; the operator override is
        // trusted config, so a bad value is loud.) Unset / registered → a no-op.
        AgentHarnessDefaults.Validate(list.Select(h => h.Kind).ToList(), configuredDefault);
    }

    public IReadOnlyList<IAgentHarness> All { get; }

    public IAgentHarness Resolve(string kind)
    {
        if (!_byKind.TryGetValue(kind, out var harness))
            throw new InvalidOperationException($"No IAgentHarness registered for kind '{kind}'. Ensure the corresponding harness adapter is loaded.");

        return harness;
    }
}
