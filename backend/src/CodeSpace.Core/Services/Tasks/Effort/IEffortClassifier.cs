using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Tasks.Effort;

namespace CodeSpace.Core.Services.Tasks.Effort;

/// <summary>
/// One effort classifier — the polymorphic seam that extracts generic <see cref="EffortSignals"/> from a task
/// and emits an <see cref="EffortDecision"/> (Rule 18.3, each impl beside its variant folder under
/// <c>Effort/Classifiers/&lt;Strategy&gt;/</c>). A classifier emits DATA; the POLICY decides the tier — so the
/// heuristic baseline and the (deferred) structured-LLM classifier differ only in signal quality + confidence,
/// never in the routing logic. Self-registers via the <see cref="ISingletonDependency"/> marker, so a new
/// strategy is a sibling folder with ZERO edit to the registry / router (Rule 7 — a sibling impl, never a wider
/// interface).
/// </summary>
public interface IEffortClassifier
{
    /// <summary>The classifier kind this impl is — the open string the registry indexes + resolves it by (e.g. <c>"heuristic"</c>). Mirrors <c>IAgentHarness.Kind</c>.</summary>
    string Kind { get; }

    /// <summary>Classify <paramref name="request"/> into an effort decision (signals + suggested tier + recipe + confidence + provenance). Pure of routing logic — it suggests, the policy / router decide.</summary>
    Task<EffortDecision> ClassifyAsync(EffortRouteRequest request, CancellationToken ct);
}
