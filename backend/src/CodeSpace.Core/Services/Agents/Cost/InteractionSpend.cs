using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;
using CodeSpace.Core.Services.Workflows.Llm;

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
/// own no-DB contract). Missing, invalid or partial usage and unrepresentable prices stay unknown, never an
/// invented zero. Known token subtotals can still be displayed without claiming that they describe the full bill.</para>
/// </summary>
public static class InteractionSpend
{
    /// <summary>Price one <c>interaction.completed</c> row against the operator's per-row price table (D1; null = env + built-in tables only). <see cref="CostUsd"/> is null when the model is unknown/unpriceable (fail-open, mirrors <see cref="AgentCostPricing.CostUsd"/>'s own null contract) — the caller sums only the known ones.</summary>
    public static InteractionSpendRow From(WorkflowRunRecord record, IReadOnlyDictionary<string, ModelPrice>? rowPrices = null)
    {
        var kind = ReadString(record, "kind");
        var model = ReadString(record, "model");
        var usage = ReadUsage(record);

        return new InteractionSpendRow
        {
            Kind = string.IsNullOrWhiteSpace(kind) ? "unknown" : kind!,
            Model = model,
            InputTokens = usage.InputTokens ?? 0,
            OutputTokens = usage.OutputTokens ?? 0,
            CostUsd = LlmUsageCost.Usd(model, usage, rowPrices),
        };
    }

    private static LlmUsage ReadUsage(WorkflowRunRecord record)
    {
        try
        {
            using var doc = JsonDocument.Parse(record.PayloadJson);

            if (!doc.RootElement.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return LlmUsage.None;

            return new LlmUsage
            {
                InputTokens = ReadInt(usage, "inputTokens"),
                OutputTokens = ReadInt(usage, "outputTokens"),
                IsPartial = usage.TryGetProperty("isPartial", out var partial) && partial.ValueKind != JsonValueKind.False,
            };
        }
        catch (JsonException)
        {
            return LlmUsage.None;
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

    /// <summary>D1 — the FIRST named model whose brain-plane spend could NOT be priced, or null when every row priced. Under a cost cap this is not a cosmetic qualifier: it means <see cref="TotalUsd"/> UNDERSTATES the bill, so the cap cannot be enforced and the run must fail closed (<see cref="UnpricedModelUnderCap"/>).</summary>
    public string? UnpricedModel { get; init; }

    public static readonly BrainPlaneSpendSummary Empty = new();

    /// <summary>Fold a set of priced rows into the summary — sums ONLY the known-cost rows (fail-open), grouped by kind, and NAMES the first unpriceable model so a capped run can refuse to spend blind.</summary>
    public static BrainPlaneSpendSummary From(IReadOnlyList<InteractionSpendRow> rows)
    {
        var unpriced = rows.FirstOrDefault(r => r.CostUsd is null && !string.IsNullOrWhiteSpace(r.Model))?.Model;
        var known = rows.Where(r => r.CostUsd is not null).ToList();

        if (known.Count == 0) return unpriced is null ? Empty : new BrainPlaneSpendSummary { UnpricedModel = unpriced };

        var byKind = known
            .GroupBy(r => r.Kind, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.CostUsd!.Value), StringComparer.Ordinal);

        return new BrainPlaneSpendSummary { TotalUsd = byKind.Values.Sum(), ByKind = byKind, UnpricedModel = unpriced };
    }
}
