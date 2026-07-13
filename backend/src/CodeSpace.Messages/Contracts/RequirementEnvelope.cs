using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Contracts;

/// <summary>
/// "What this run OWES" — the kind-agnostic envelope the completion kernel reads (F0 / v4.1). The kernel never
/// opens a kind's payload: the payload lives at <see cref="SpecRef"/> as canonical bytes whose
/// <see cref="SpecHash"/> (canonical-json-v1, v4.1-B) is what receipts and human co-signs bind to — a mutable
/// reference can never launder a later-edited spec under an earlier approval. Null-omitted throughout so the
/// serialized shape stays byte-stable as optional fields arrive.
/// </summary>
public sealed record RequirementEnvelope
{
    /// <summary>Stable id within the contract (a unit-level acceptance derives from its subtask id; run-level delivery/output use fixed keys).</summary>
    public required string RequirementRef { get; init; }

    /// <summary>The registry key (<see cref="ContractKinds"/>) that names which payload type <see cref="SpecRef"/> holds.</summary>
    public required string Kind { get; init; }

    public required Requiredness Requiredness { get; init; }

    /// <summary>Who stands behind this requirement — a model may propose, never authorize (see <see cref="ContractAuthority"/>).</summary>
    public required ContractAuthority Authority { get; init; }

    /// <summary>CAS reference to the spec's canonical bytes (v4.1-B). Null only for a kind whose spec is fully carried by the envelope itself.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? SpecRef { get; init; }

    /// <summary>canonical-json-v1 hash over the FULL spec identity (v4.1-B) — the value receipts and co-signs bind to.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SpecHash { get; init; }

    /// <summary>Expected receipt cardinality for a one-contract-many-targets requirement (a multi-repo delivery = N repos). Null → 1.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ExpectedCardinality { get; init; }

    /// <summary>The envelope's own schema version — a protocol change must be explicit, never a silent shape drift (the M1a suite-version discipline).</summary>
    public required string ContractSchemaVersion { get; init; }
}
