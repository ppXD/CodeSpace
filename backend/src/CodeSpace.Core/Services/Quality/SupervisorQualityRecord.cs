using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Quality;

namespace CodeSpace.Core.Services.Quality;

/// <summary>
/// The codec for the durable <c>supervisor_decision.quality_decisions</c> column (P22-9b) — the per-unit
/// recommendations the turn that emitted a decision was looking at, frozen onto that decision's row. One place
/// serializes and one place reads, through the same <see cref="AgentJson.Options"/> every other supervisor payload
/// uses, so the bytes the Room renders are the bytes the prompt carried.
///
/// <para>Why it is worth a column at all: a recommendation the model may reject is only auditable if what it was
/// shown survives the turn. Without the row, "the policy said escalate and the brain retried anyway" is not
/// recoverable after the fact — and 9c's ablation is precisely a comparison between what the policy recommended and
/// what the run did.</para>
///
/// <para>Both directions FAIL SOFT. A write of nothing is <c>null</c> (the column stays NULL rather than storing an
/// empty array, so a pre-spawn decision's row is byte-identical to every row written before the column existed),
/// and a read of NULL, of a legacy row, or of bytes a future shape wrote is the empty list — a decision row is
/// audit, and no read of it may ever throw a live turn or a Room projection down.</para>
/// </summary>
public static class SupervisorQualityRecord
{
    /// <summary>The recommendations as durable JSON, or null when there are none.</summary>
    public static string? ToJson(IReadOnlyList<SupervisorUnitQualityDecision> decisions) =>
        decisions.Count == 0 ? null : JsonSerializer.Serialize(decisions, AgentJson.Options);

    /// <summary>The recommendations a row recorded — empty for a NULL column, a legacy row, or unreadable bytes.</summary>
    public static IReadOnlyList<SupervisorUnitQualityDecision> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<SupervisorUnitQualityDecision>();

        try { return (IReadOnlyList<SupervisorUnitQualityDecision>?)JsonSerializer.Deserialize<List<SupervisorUnitQualityDecision>>(json, AgentJson.Options) ?? Array.Empty<SupervisorUnitQualityDecision>(); }
        catch (JsonException) { return Array.Empty<SupervisorUnitQualityDecision>(); }
    }
}
