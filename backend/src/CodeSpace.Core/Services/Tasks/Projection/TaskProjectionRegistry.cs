using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Tasks.Projection;

/// <summary>
/// Default <see cref="ITaskProjectionRegistry"/> — indexes every registered <see cref="IWorkflowDefinitionBuilder"/>
/// by its <see cref="IWorkflowDefinitionBuilder.ProjectionKind"/>. Mirrors <c>AgentHarnessRegistry</c> /
/// <c>SandboxRunnerRegistry</c> EXACTLY: DI injects all builders, this dedups (a duplicate kind throws in the
/// ctor) + resolves (an unknown kind throws). Registered automatically via the <see cref="ISingletonDependency"/>
/// marker, so adding a builder needs no wiring here — the generic dispatch is <c>Resolve(openString)</c> with
/// zero core switch.
/// </summary>
public sealed class TaskProjectionRegistry : ITaskProjectionRegistry, ISingletonDependency
{
    private readonly IReadOnlyDictionary<string, IWorkflowDefinitionBuilder> _byKind;

    public TaskProjectionRegistry(IEnumerable<IWorkflowDefinitionBuilder> builders)
    {
        var list = builders.ToList();

        var duplicates = list.GroupBy(b => b.ProjectionKind).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        if (duplicates.Count > 0)
            throw new InvalidOperationException($"Duplicate IWorkflowDefinitionBuilder projection kinds: {string.Join(", ", duplicates)}");

        _byKind = list.ToDictionary(b => b.ProjectionKind);
        Kinds = list.Select(b => b.ProjectionKind).ToList();
    }

    public IReadOnlyList<string> Kinds { get; }

    public IWorkflowDefinitionBuilder Resolve(string projectionKind)
    {
        if (!_byKind.TryGetValue(projectionKind, out var builder))
            throw new InvalidOperationException($"No IWorkflowDefinitionBuilder registered for projection kind '{projectionKind}'. Drop a Projection/Builders/<Kind>/ impl that self-registers.");

        return builder;
    }

    public bool TryResolve(string projectionKind, out IWorkflowDefinitionBuilder builder) =>
        _byKind.TryGetValue(projectionKind, out builder!);
}
