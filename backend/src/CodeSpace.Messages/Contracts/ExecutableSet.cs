using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Contracts;

/// <summary>How a unit of the CURRENT plan version relates to the previous version (P1b / P+). Receipts never cross versions regardless (unit keys are plan-version-aware); <see cref="Carried"/> marks content-identical units so a future server-authored CarryAuthorization (P3a/R) can explicitly bridge them — without one, Carried is metadata, never permission.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UnitDisposition
{
    New,
    Carried,
    Replaced,
}

/// <summary>One executable unit of the current plan version: its plan-local id, its PLAN-GRAIN contract hash (no dispatch overrides — those are attempt-grain), and how it relates to the previous version.</summary>
public sealed record ExecutableUnit
{
    public required string UnitId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContractHash { get; init; }

    public required UnitDisposition Disposition { get; init; }
}

/// <summary>
/// THE CurrentExecutableSet (P1b / Lock Clauses 2+3): which units are executable under the current plan version.
/// A cancelled unit (present in the prior version, absent from the current) is NOT in the set — its attempts and
/// receipts can never participate in the current assessment; they are listed for diagnostics only.
/// <see cref="SetHash"/> is the canonical digest of the set's identity (plan row + version + ordered unit
/// contract hashes) — the <c>CurrentExecutableSetHash</c> watermark every assessment binds to (Lock Clause 2).
/// A lane with no natural plan uses <see cref="SyntheticRoot"/>: the RUN id stands in as the plan id and version
/// 0 marks the set synthetic (real plan versions start at 1) — Enforced modes never run with a null set
/// (Lock Clause 3), they run with a synthetic one.
/// </summary>
public sealed record ExecutableSet
{
    /// <summary>The plan-local well-known unit id of a synthetic root set.</summary>
    public const string RootUnitId = "root";

    public required Guid WorkPlanId { get; init; }

    public required int PlanVersion { get; init; }

    public required IReadOnlyList<ExecutableUnit> Units { get; init; }

    /// <summary>Prior-version unit ids no longer executable — diagnostics only, never members.</summary>
    public IReadOnlyList<string> CancelledUnitIds { get; init; } = Array.Empty<string>();

    /// <summary>canonical-json-v1 digest of the set's identity — the CurrentExecutableSetHash watermark (Lock Clause 2).</summary>
    public required string SetHash { get; init; }

    public static ExecutableSet Create(Guid workPlanId, int planVersion, IReadOnlyList<ExecutableUnit> units, IReadOnlyList<string>? cancelledUnitIds = null) => new()
    {
        WorkPlanId = workPlanId,
        PlanVersion = planVersion,
        Units = units,
        CancelledUnitIds = cancelledUnitIds ?? Array.Empty<string>(),
        SetHash = ContractHashing.Hash(System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new
        {
            workPlanId,
            planVersion,
            units = units.OrderBy(u => u.UnitId, StringComparer.Ordinal).Select(u => new { u.UnitId, u.ContractHash }),
        })).RootElement),
    };

    /// <summary>The synthetic single-unit set for a plan-less lane: the RUN id as plan id, version 0, one root unit.</summary>
    public static ExecutableSet SyntheticRoot(Guid workflowRunId, string? contractHash) =>
        Create(workflowRunId, planVersion: 0, new[] { new ExecutableUnit { UnitId = RootUnitId, ContractHash = contractHash, Disposition = UnitDisposition.New } });

    public bool Contains(string unitId) => Units.Any(u => u.UnitId == unitId);
}
