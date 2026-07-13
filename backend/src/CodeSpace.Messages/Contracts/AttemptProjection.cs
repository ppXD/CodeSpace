using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Contracts;

/// <summary>Where an attempt stands in its lifecycle (P1b). <see cref="Authorized"/> = server-staged, evidence not yet durable; <see cref="Settled"/> = its terminal result is a durable tape fact. Supersession is DERIVED by the selectors (highest authorization ordinal per unit), never stored.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AttemptState
{
    Authorized,
    Settled,
}

/// <summary>
/// The GENERIC per-attempt projection (P1b) every lane adapter emits and both selectors consume — the single
/// vocabulary for "which attempts exist for which unit", so the completion composer, CES, and P+ can never invent
/// divergent attempt rules per lane. <see cref="AttemptOrdinal"/> is the server-side AUTHORIZATION order per unit
/// (1-based; a GUID id can never carry order); <see cref="WorkUnit"/> is the full plan-bound identity when the
/// lane stamped one (Lock Clause 3: null tolerated only for Legacy/Shadow tapes — an Enforced mode without it
/// parks); <see cref="UnitId"/> always carries the plan-local unit key so even legacy attempts group correctly,
/// and unit keys are PLAN-VERSION-AWARE downstream (s1@plan-v1 never satisfies s1@plan-v2).
/// </summary>
public sealed record AttemptProjection
{
    /// <summary>The attempt's durable id (the staged AgentRun id on the agent lanes).</summary>
    public required Guid AttemptId { get; init; }

    /// <summary>The plan-local unit this attempt ran — always present, the grouping key of last resort.</summary>
    public required string UnitId { get; init; }

    /// <summary>Full plan-bound identity (plan row + version + unit + contract hash) when the lane stamped one; null only on Legacy/Shadow tapes.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkUnitRef? WorkUnit { get; init; }

    /// <summary>1-based server-side authorization order within this attempt's unit key — THE order authority for both selectors.</summary>
    public required int AttemptOrdinal { get; init; }

    /// <summary>The P+ execution generation this attempt was authorized under; null until the generation machinery lands (P1b-3+).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Generation { get; init; }

    public required AttemptState State { get; init; }
}
