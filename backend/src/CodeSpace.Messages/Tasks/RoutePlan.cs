namespace CodeSpace.Messages.Tasks;

/// <summary>
/// The ROUTING DECISION for a task — the effort / recipe / projection the router chose, plus the bounds and
/// the human-in-the-loop posture (Rule 18.1, a pure data noun). The one field the projection layer reads to
/// dispatch is <see cref="ProjectionKind"/> (an OPEN STRING the registry resolves a builder by); the rest is
/// provenance + bounds a later phase consumes. The classifier / router that PRODUCES a RoutePlan is PR3 —
/// PR2 constructs it directly (e.g. in tests) to drive the projection.
/// </summary>
public sealed record RoutePlan
{
    /// <summary>The effort mode the router chose (e.g. <c>"quick"</c>, <c>"deep"</c>) — an open string, provenance for the projection.</summary>
    public string EffortMode { get; init; } = "";

    /// <summary>The recipe the router chose (e.g. <c>"bugfix"</c>) — an open string, provenance for the projection.</summary>
    public string RecipeKind { get; init; } = "";

    /// <summary>The projection strategy to build the run with — an OPEN STRING the <c>ITaskProjectionRegistry</c> resolves a builder by (see <see cref="TaskProjectionKinds"/>). The single load-bearing field for dispatch.</summary>
    public required string ProjectionKind { get; init; }

    /// <summary>The bounds preset the router selected (e.g. <c>"standard"</c>) — an open string naming where <see cref="Caps"/> came from.</summary>
    public string BoundsPreset { get; init; } = "";

    /// <summary>The concrete safety bounds the projected run runs under. Defaults to an empty (no-explicit-cap) preset.</summary>
    public RouteCaps Caps { get; init; } = new();

    /// <summary>The autonomy tier recommended for the run, as an open tier-name string (e.g. <c>"Standard"</c>).</summary>
    public string RecommendedAutonomy { get; init; } = "";

    /// <summary>Whether the launch flow should show a confirm card before running. Default false.</summary>
    public bool NeedsConfirmCard { get; init; }

    /// <summary>Whether the run should pause for a plan review before executing. Default false.</summary>
    public bool NeedsPlanReview { get; init; }

    /// <summary>Whether this route was chosen by the auto-classifier (vs explicitly by the surface / operator). Default false.</summary>
    public bool WasAutoClassified { get; init; }

    /// <summary>The classifier's confidence in the auto-route, 0..1. Default 0 (not auto-classified).</summary>
    public double ClassifierConfidence { get; init; }

    /// <summary>When the router fell back to a degraded route, the reason why; null on the happy path.</summary>
    public string? DegradedReason { get; init; }
}
