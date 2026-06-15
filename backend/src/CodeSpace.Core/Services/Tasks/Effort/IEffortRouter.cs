using CodeSpace.Messages.Tasks;
using CodeSpace.Messages.Tasks.Effort;

namespace CodeSpace.Core.Services.Tasks.Effort;

/// <summary>
/// The L2 effort router — turns an <see cref="EffortRouteRequest"/> into a <see cref="RoutePlan"/> the PR2
/// projection layer consumes. It composes the three open-string registries (classifier / recipe / bounds) +
/// the pure <c>EffortPolicy</c>, naming NO concrete classifier / recipe / preset type: an explicit operator
/// effort short-circuits the classifier; otherwise the default classifier emits a decision the policy turns into
/// a tier, the recipe (fail-open to the default) supplies the projection + bounds fallback, and the bounds
/// preset (resolved by the effort mode) supplies the caps. A below-floor auto-classification produces a confirm
/// card whose options are DERIVED from the bounds registry — so a new classifier / recipe / preset plugs in with
/// zero router edit (the generic dispatch spine).
/// </summary>
public interface IEffortRouter
{
    /// <summary>Route <paramref name="request"/> into a <see cref="RoutePlan"/>. Never throws on an unknown suggested recipe / bounds kind — it fails OPEN to the safe default recipe + empty caps.</summary>
    Task<RoutePlan> RouteAsync(EffortRouteRequest request, CancellationToken ct);
}
