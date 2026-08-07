using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Contracts;

/// <summary>
/// The plan-lineage stamp (F0 / P+) — WHICH plan version's WHICH unit an attempt worked under, plus the contract
/// identity it was dispatched against. A receipt carrying a superseded <see cref="PlanVersion"/> never satisfies
/// the current plan (the newest-plan anchoring the amend-acceptance B0 ruling standardized), and a
/// <see cref="ContractHash"/> mismatch means the unit's contract was amended after dispatch — the attempt's
/// receipts describe an obligation that no longer exists as written.
/// </summary>
public sealed record WorkUnitRef
{
    public required Guid WorkPlanId { get; init; }

    public required int PlanVersion { get; init; }

    public required string UnitId { get; init; }

    /// <summary>canonical-json-v1 hash of the unit's contract at dispatch (v4.1-B). Null for a contract-less unit.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContractHash { get; init; }

    /// <summary>
    /// P1 (v4.3): the unit's ACCEPTANCE requirement revision this attempt was dispatched under — the ledger row the
    /// dispatch-time stake produced (or, on an idempotent crash replay, the one it already had). Identity where
    /// <see cref="ContractHash"/> is value: a revert-shaped amendment (A→B→A) collides on hash but never on
    /// revision. The delivery/output rows of the same stake wave share the acceptance row's staleness, so ONE
    /// revision names the wave. Null on legacy stamps and plan-less dispatches (null-omitted — byte-stable).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? RequirementRevision { get; init; }
}
