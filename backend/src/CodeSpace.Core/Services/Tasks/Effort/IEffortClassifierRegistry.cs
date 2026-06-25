namespace CodeSpace.Core.Services.Tasks.Effort;

/// <summary>
/// Resolves an <see cref="IEffortClassifier"/> by its <see cref="IEffortClassifier.Kind"/> — same shape as
/// <c>IAgentHarnessRegistry</c> / <c>ITaskProjectionRegistry</c>, plus a <see cref="Default"/> the router uses
/// as its deterministic classifier when no specific kind is requested. A new classifier strategy becomes
/// resolvable by registering its class — no edit here. The router never names a concrete classifier type; it
/// asks this registry for <see cref="Default"/>, so swapping the heuristic baseline for the (deferred)
/// structured-LLM classifier is a registration change, not a router edit.
/// </summary>
public interface IEffortClassifierRegistry
{
    /// <summary>Every registered classifier — the "which classifiers are available" surface.</summary>
    IReadOnlyList<IEffortClassifier> All { get; }

    /// <summary>Resolve the classifier for <paramref name="kind"/>. Throws when none is registered for that kind.</summary>
    IEffortClassifier Resolve(string kind);

    /// <summary>Try-resolve variant — false (and a null out) when no classifier is registered for <paramref name="kind"/>.</summary>
    bool TryResolve(string kind, out IEffortClassifier classifier);

    /// <summary>The deterministic baseline classifier (the always-confirm heuristic) — the GUARANTEED floor: the registry requires it, and the structured-LLM classifier degrades to it when no model is available.</summary>
    IEffortClassifier Default { get; }

    /// <summary>The PREFERRED auto-path classifier the router asks for — the structured-LLM one when registered (a real model decision that can clear the confirm floor), else <see cref="Default"/>. So a deployment with a model supersedes the always-confirm baseline with ZERO router edit.</summary>
    IEffortClassifier Auto { get; }
}
