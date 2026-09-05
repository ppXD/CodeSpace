namespace CodeSpace.Messages.Tasks;

/// <summary>
/// The result of a READ-ONLY route preview (Rule 18.1, a pure data noun) — the <see cref="RoutePlan"/> the very
/// same router would produce for this request if it were launched right now. Nothing was opened, staged or
/// persisted to compute it.
///
/// <para>The load-bearing fields for the composer are <c>NeedsConfirmCard</c> + <c>Confirm</c>: the router builds
/// a confirm card whenever an AUTO route was classified below the confidence floor OR the classifier flagged
/// risky side effects, and before this preview existed nothing ever showed it — the launch ran regardless. The
/// composer now renders that card BEFORE the launch and the operator answers it by launching at an EXPLICIT
/// tier, which short-circuits the classifier and routes deterministically.</para>
/// </summary>
public sealed record TaskRoutePreviewResult
{
    /// <summary>The route this request would take — effort / recipe / projection / bounds plus the classifier decision, the confirm card and any degrade reason. Never null: the router always decides something (its classifier degrades to the deterministic baseline rather than failing).</summary>
    public required RoutePlan Route { get; init; }

    /// <summary>
    /// This deployment's own autonomy ceiling (<c>Sandbox:MaxAutonomy</c>) — the tier no run on this host may exceed,
    /// whatever the composer sends. It is already folded into <see cref="RoutePlan.Caps"/>'s ceiling, and is repeated
    /// here NAMED because the composer's network-posture line has to say WHICH bound denied the network: a route
    /// ceiling the operator can lift by picking a different effort tier, or this one, which they cannot.
    /// </summary>
    public required string DeploymentAutonomyCeiling { get; init; }
}
