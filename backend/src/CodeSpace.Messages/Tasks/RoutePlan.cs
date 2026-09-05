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

    /// <summary>
    /// WHAT this task is asked to produce, as an open <c>DeliverableShapes</c> string (<c>answer</c> / <c>document</c>
    /// / <c>code</c> / <c>research</c>) — carried FIRST-CLASS off the classifier's signals so the projection layer can
    /// route by shape, not only by effort: the shape decides the projected agent's mode and (when the operator authored
    /// no executable floor) which objective oracle grades it. Defaults to <c>code</c> — the historical assumption — so
    /// a hand-built route and an explicit-tier launch project byte-identically.
    /// </summary>
    public string DeliverableShape { get; init; } = Effort.DeliverableShapes.Code;

    /// <summary>The bounds preset the router selected (e.g. <c>"standard"</c>) — an open string naming where <see cref="Caps"/> came from.</summary>
    public string BoundsPreset { get; init; } = "";

    /// <summary>The concrete safety bounds the projected run runs under. Defaults to an empty (no-explicit-cap) preset.</summary>
    public RouteCaps Caps { get; init; } = new();

    /// <summary>The autonomy tier recommended for the run, as an open tier-name string (e.g. <c>"Standard"</c>).</summary>
    public string RecommendedAutonomy { get; init; } = "";

    /// <summary>
    /// The tier the run ACTUALLY got — <c>Clamp(requested, Caps.AutonomyCeiling)</c>, as an open tier-name string.
    /// The router NEVER sets this (it does not see the operator's request): <c>TaskRunSnapshotFactory</c> stamps it
    /// onto the run's route provenance from the resolved agent profile, so <c>route_plan_jsonb</c> — the run's
    /// launch-provenance column — records not only what the route ALLOWED but what the launch RESOLVED to. Without it
    /// a reader can see the ceiling and never say whether network was declined or denied. Blank on a plan that was
    /// never stamped onto a run (a preview, a hand-built route), and blank on every run staged before this field
    /// existed — readers must treat blank as "unknown", never as a tier.
    /// </summary>
    public string EffectiveAutonomy { get; init; } = "";

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

    /// <summary>The classifier's full decision (signals + suggestions + provenance) the router routed from (PR3). Null when no classifier ran (an explicit operator effort) or for a hand-built route (PR2 tests).</summary>
    public Effort.EffortDecision? Decision { get; init; }

    /// <summary>The confirm card to show before running, when the route was auto-classified below the confidence floor (PR3). Null when no confirmation is needed.</summary>
    public Effort.ConfirmCard? Confirm { get; init; }
}
