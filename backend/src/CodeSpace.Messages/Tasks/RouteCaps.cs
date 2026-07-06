using System.Text.Json;

namespace CodeSpace.Messages.Tasks;

/// <summary>
/// The SAFETY BOUNDS a route imposes on the projected run (Rule 18.1, a pure data noun) — the fail-closed
/// caps a later phase enforces. Every numeric cap is OPTIONAL (null = the projection's own default);
/// <see cref="AutonomyCeiling"/> is an OPEN STRING (an autonomy-tier name the projection clamps to);
/// <see cref="Extra"/> carries forward-compatible bound knobs without widening this record (Rule 7).
/// PR2 constructs these in tests; the router that derives them is a later PR.
/// </summary>
public sealed record RouteCaps
{
    /// <summary>Max branches a fan-out projection may run at once. Null = the projection default.</summary>
    public int? MaxParallelism { get; init; }

    /// <summary>Max agents the run may spawn in total. Null = the projection default.</summary>
    public int? MaxTotalSpawns { get; init; }

    /// <summary>Max spend the run is allowed. Null = no cost cap at this layer.</summary>
    public decimal? MaxCostUsd { get; init; }

    /// <summary>The highest autonomy tier the run may use, as an open tier-name string (e.g. <c>"Standard"</c>) — the projection clamps to it.</summary>
    public string AutonomyCeiling { get; init; } = "";

    /// <summary>Whether the run's side effects require a human approval. Default false.</summary>
    public bool RequiresApproval { get; init; }

    /// <summary>Forward-compatible extra bound knobs (open key→value map) so a new cap needn't widen this record. Defaults empty.</summary>
    public IReadOnlyDictionary<string, JsonElement> Extra { get; init; } = new Dictionary<string, JsonElement>();
}
