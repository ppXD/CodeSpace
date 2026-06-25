using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Tasks.Effort.Classifiers.Heuristic;
using CodeSpace.Core.Services.Tasks.Effort.Classifiers.Llm;

namespace CodeSpace.Core.Services.Tasks.Effort;

/// <summary>
/// Default <see cref="IEffortClassifierRegistry"/> — indexes every registered <see cref="IEffortClassifier"/>
/// by its <see cref="IEffortClassifier.Kind"/>. Mirrors <c>AgentHarnessRegistry</c> / <c>TaskRecipeRegistry</c>
/// EXACTLY: DI injects all classifiers, this dedups (a duplicate kind throws in the ctor) + resolves (an unknown
/// kind throws; <see cref="TryResolve"/> returns false). <see cref="Default"/> is the heuristic classifier,
/// resolved from the indexed dict in the ctor (a clear error if it is absent) — the deterministic baseline the
/// router falls to when no specific classifier is requested. <see cref="IScopedDependency"/> (so it never CAPTURES the
/// scoped structured-LLM classifier + its per-request DbContext — its only consumer, the <c>EffortRouter</c>, is itself
/// scoped), so adding a classifier needs no wiring here — the dispatch is <c>Resolve(openString)</c> with zero
/// per-kind switch.
/// </summary>
public sealed class EffortClassifierRegistry : IEffortClassifierRegistry, IScopedDependency
{
    private readonly IReadOnlyDictionary<string, IEffortClassifier> _byKind;

    public EffortClassifierRegistry(IEnumerable<IEffortClassifier> classifiers)
    {
        var list = classifiers.ToList();

        var duplicates = list.GroupBy(c => c.Kind).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        if (duplicates.Count > 0)
            throw new InvalidOperationException($"Duplicate IEffortClassifier kinds: {string.Join(", ", duplicates)}");

        _byKind = list.ToDictionary(c => c.Kind);
        All = list;

        if (!_byKind.TryGetValue(HeuristicEffortClassifier.ClassifierKind, out var fallback))
            throw new InvalidOperationException($"The default classifier '{HeuristicEffortClassifier.ClassifierKind}' is not registered — the heuristic baseline must exist for the router's auto-path fallback.");

        Default = fallback;

        // Prefer the structured-LLM classifier on the auto path when it is registered (a real model decision), else the
        // always-confirm heuristic baseline — the router asks for Auto, so this selection supersedes the baseline with no
        // router edit. The LLM classifier itself degrades to Default at run time when no model is available.
        Auto = _byKind.TryGetValue(LlmEffortClassifier.ClassifierKind, out var llm) ? llm : Default;
    }

    public IReadOnlyList<IEffortClassifier> All { get; }

    public IEffortClassifier Default { get; }

    public IEffortClassifier Auto { get; }

    public IEffortClassifier Resolve(string kind)
    {
        if (!_byKind.TryGetValue(kind, out var classifier))
            throw new InvalidOperationException($"No IEffortClassifier registered for kind '{kind}'. Drop an Effort/Classifiers/<Strategy>/ impl that self-registers.");

        return classifier;
    }

    public bool TryResolve(string kind, out IEffortClassifier classifier) =>
        _byKind.TryGetValue(kind, out classifier!);
}
