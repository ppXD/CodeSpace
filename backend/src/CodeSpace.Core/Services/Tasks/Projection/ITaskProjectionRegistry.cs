namespace CodeSpace.Core.Services.Tasks.Projection;

/// <summary>
/// Resolves an <see cref="IWorkflowDefinitionBuilder"/> by its <see cref="IWorkflowDefinitionBuilder.ProjectionKind"/>
/// — same shape as <c>IAgentHarnessRegistry</c> / <c>ISandboxRunnerRegistry</c>. The policy that decides WHICH
/// projection a task uses (the router) lives in the caller; this registry only maps a kind → its builder. A new
/// builder becomes resolvable by registering its class — no edit here.
/// </summary>
public interface ITaskProjectionRegistry
{
    /// <summary>Every registered projection kind — for the "which projections are available" surface.</summary>
    IReadOnlyList<string> Kinds { get; }

    /// <summary>Resolve the builder for <paramref name="projectionKind"/>. Throws when none is registered for that kind.</summary>
    IWorkflowDefinitionBuilder Resolve(string projectionKind);

    /// <summary>Try-resolve variant — false (and a null out) when no builder is registered for <paramref name="projectionKind"/>, for callers that branch rather than throw.</summary>
    bool TryResolve(string projectionKind, out IWorkflowDefinitionBuilder builder);
}
