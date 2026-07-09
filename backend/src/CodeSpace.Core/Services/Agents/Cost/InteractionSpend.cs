using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;

namespace CodeSpace.Core.Services.Agents.Cost;

/// <summary>
/// P3.5 — prices ONE <c>interaction.completed</c> ledger row (<see cref="WorkflowRunRecord"/>, written by
/// <c>RecordingLLMClientDecorator</c>/<c>RecordingStructuredLLMClientDecorator</c> for EVERY in-process model call —
/// the supervisor's own decision, a critic/reviewer review, a plan-authoring call, an acceptance-grading judge, any
/// future one) into a priced spend row keyed by its open <c>kind</c> label (e.g. <c>"supervisor.decision"</c>,
/// <c>"critic.review"</c>, <c>"grader.acceptance"</c>) — the SAME pricer (<see cref="AgentCostPricing"/>) the agent-
/// execution side already uses, so a brain-plane dollar and an agent-execution dollar can never disagree.
///
/// <para>Pure + static (no DB) — the row is ALREADY fetched by the caller (mirrors <see cref="AgentCostPricing"/>'s
/// own no-DB contract). Fail-open on a malformed/unpriceable payload: a missing kind/model/usage prices to 0, never
/// throws — a usage-silent call can never crash the fold nor spuriously inflate the bill.</para>
/// </summary>
public static class InteractionSpend
{
    /// <summary>Price one <c>interaction.completed</c> row. <see cref="CostUsd"/> is null when the model is unknown/unpriceable (fail-open, mirrors <see cref="AgentCostPricing.CostUsd"/>'s own null contract) — the caller sums only the known ones.</summary>
    public static InteractionSpendRow From(WorkflowRunRecord record)
    {
        var kind = ReadString(record, "kind");
        var model = ReadString(record, "model");
        var (input, output) = ReadUsage(record);

        return new InteractionSpendRow
        {
            Kind = string.IsNullOrWhiteSpace(kind) ? "unknown" : kind!,
            Model = model,
            InputTokens = input ?? 0,
            OutputTokens = output ?? 0,
            CostUsd = string.IsNullOrWhiteSpace(model) ? null : AgentCostPricing.CostUsd(model, input ?? 0, output ?? 0),
        };
    }

    private static (int? Input, int? Output) ReadUsage(WorkflowRunRecord record)
    {
        try
        {
            using var doc = JsonDocument.Parse(record.PayloadJson);

            if (!doc.RootElement.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return (null, null);

            return (ReadInt(usage, "inputTokens"), ReadInt(usage, "outputTokens"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string? ReadString(WorkflowRunRecord record, string prop)
    {
        try
        {
            using var doc = JsonDocument.Parse(record.PayloadJson);

            return doc.RootElement.TryGetProperty(prop, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? ReadInt(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n) ? n : null;
}

/// <summary>One priced <c>interaction.completed</c> row — the kind label, the model, its tokens, and its USD cost (null = unpriceable, fail-open).</summary>
public sealed record InteractionSpendRow
{
    public required string Kind { get; init; }
    public string? Model { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public decimal? CostUsd { get; init; }
}

/// <summary>The summed brain-plane spend for a run: the total USD (known rows only, fail-open) + a per-KIND breakdown (e.g. <c>{"supervisor.decision": 3.20, "critic.review": 1.14}</c>) for recitation/detail rendering. A kind with only unpriceable rows is simply absent from <see cref="ByKind"/> (never a bogus $0 entry).</summary>
public sealed record BrainPlaneSpendSummary
{
    public decimal TotalUsd { get; init; }
    public IReadOnlyDictionary<string, decimal> ByKind { get; init; } = new Dictionary<string, decimal>();

    public static readonly BrainPlaneSpendSummary Empty = new();

    /// <summary>Fold a set of priced rows into the summary — sums ONLY the known-cost rows (fail-open), grouped by kind.</summary>
    public static BrainPlaneSpendSummary From(IReadOnlyList<InteractionSpendRow> rows)
    {
        var known = rows.Where(r => r.CostUsd is not null).ToList();

        if (known.Count == 0) return Empty;

        var byKind = known
            .GroupBy(r => r.Kind, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.CostUsd!.Value), StringComparer.Ordinal);

        return new BrainPlaneSpendSummary { TotalUsd = byKind.Values.Sum(), ByKind = byKind };
    }
}
