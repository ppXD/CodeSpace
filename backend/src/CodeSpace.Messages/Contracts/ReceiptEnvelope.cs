using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Contracts;

/// <summary>
/// "What HAPPENED against one requirement" — the kind-agnostic receipt the completion kernel reads (F0 / v4.1).
/// The kernel is Git/PR/artifact-agnostic by construction: it sees dispositions, hashes, and lineage — never a
/// branch name's meaning. Null-omitted throughout.
/// </summary>
public sealed record ReceiptEnvelope
{
    /// <summary>Which requirement this receipt answers (<see cref="RequirementEnvelope.RequirementRef"/>).</summary>
    public required string RequirementRef { get; init; }

    /// <summary>The plan-lineage stamp — a superseded plan's receipt never satisfies the current plan (P+).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkUnitRef? WorkUnit { get; init; }

    /// <summary>@1 ≡ the first AUTHORIZED attempt (the M1a definition) — never best-of-N, never a human-corrected re-run.</summary>
    public required Guid AttemptId { get; init; }

    /// <summary>The P+ EXECUTION GENERATION this receipt was produced under — a fenced-out generation's receipt is never-counted (the fence guarantees never-authorized/never-counted, not never-happened). Named <c>Generation</c> deliberately: <c>AgentRun.FenceEpoch</c> is the WORKER-CLAIM fence (a lease CAS, bumped per re-claim) — a different fence for a different race; the two must never be conflated.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Generation { get; init; }

    /// <summary>Same registry domain as the requirement (<see cref="ContractKinds"/>).</summary>
    public required string Kind { get; init; }

    /// <summary>Content hashes of the produced artifact(s) — a branch tip sha, a patch artifact hash, a typed artifact hash.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ContentHashes { get; init; }

    /// <summary>The typed verdict — see <see cref="VerificationDisposition"/>'s mapping notes.</summary>
    public required VerificationDisposition Disposition { get; init; }

    /// <summary>Who stands behind the verdict: an oracle that ran = <see cref="ContractAuthority.ServerPolicy"/>; a human co-sign = <see cref="ContractAuthority.Operator"/>; a self-report = <see cref="ContractAuthority.ModelProposal"/>.</summary>
    public required ContractAuthority Authority { get; init; }

    /// <summary>CAS reference to the verdict's evidence (grader output, oracle log). Required-contract receipts must carry one; without evidence a disposition can be at most <see cref="VerificationDisposition.InfraUnknown"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? EvidenceRef { get; init; }

    /// <summary>Which evaluator version produced the verdict (a Q-freeze item) — a verdict from a superseded evaluator is re-qualification input, not truth.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EvaluatorVersion { get; init; }

    public required DateTimeOffset ObservedAt { get; init; }
}
