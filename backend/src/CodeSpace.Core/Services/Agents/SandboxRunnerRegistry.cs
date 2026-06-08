using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Default <see cref="ISandboxRunnerRegistry"/> — indexes every registered <see cref="ISandboxRunner"/>
/// by its <see cref="ISandboxRunner.Kind"/>. Mirrors <c>LLMClientRegistry</c> / <c>OAuthClientRegistry</c>:
/// the DI container injects all runners, this dedups + resolves. Registered automatically via the
/// <see cref="ISingletonDependency"/> marker, so adding a runner needs no wiring here.
/// </summary>
public sealed class SandboxRunnerRegistry : ISandboxRunnerRegistry, ISingletonDependency
{
    private readonly IReadOnlyDictionary<string, ISandboxRunner> _byKind;

    public SandboxRunnerRegistry(IEnumerable<ISandboxRunner> runners)
    {
        var list = runners.ToList();

        var duplicates = list.GroupBy(r => r.Kind).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        if (duplicates.Count > 0)
            throw new InvalidOperationException($"Duplicate ISandboxRunner kinds: {string.Join(", ", duplicates)}");

        _byKind = list.ToDictionary(r => r.Kind);
        All = list;
    }

    public IReadOnlyList<ISandboxRunner> All { get; }

    public ISandboxRunner Resolve(string kind)
    {
        if (!_byKind.TryGetValue(kind, out var runner))
            throw new InvalidOperationException($"No ISandboxRunner registered for kind '{kind}'. Ensure the corresponding runner is loaded.");

        return runner;
    }
}
