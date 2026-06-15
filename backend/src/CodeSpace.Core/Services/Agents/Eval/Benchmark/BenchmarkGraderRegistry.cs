using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Agents.Benchmark;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark;

/// <summary>
/// Default <see cref="IBenchmarkGraderRegistry"/> — indexes every registered <see cref="IBenchmarkGrader"/> by
/// its <see cref="IBenchmarkGrader.Kind"/>. Mirrors <c>SandboxRunnerRegistry</c>: the DI container injects all
/// graders, this dedups + resolves. Registered automatically via the <see cref="ISingletonDependency"/> marker,
/// so adding a grader needs no wiring here.
/// </summary>
public sealed class BenchmarkGraderRegistry : IBenchmarkGraderRegistry, ISingletonDependency
{
    private readonly IReadOnlyDictionary<BenchmarkGradingKind, IBenchmarkGrader> _byKind;

    public BenchmarkGraderRegistry(IEnumerable<IBenchmarkGrader> graders)
    {
        var list = graders.ToList();

        var duplicates = list.GroupBy(g => g.Kind).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        if (duplicates.Count > 0)
            throw new InvalidOperationException($"Duplicate IBenchmarkGrader kinds: {string.Join(", ", duplicates)}");

        _byKind = list.ToDictionary(g => g.Kind);
    }

    public IBenchmarkGrader Resolve(BenchmarkGradingKind kind)
    {
        if (!_byKind.TryGetValue(kind, out var grader))
            throw new InvalidOperationException($"No IBenchmarkGrader registered for kind '{kind}'. It is a documented follow-on whose grader is not built yet.");

        return grader;
    }
}
