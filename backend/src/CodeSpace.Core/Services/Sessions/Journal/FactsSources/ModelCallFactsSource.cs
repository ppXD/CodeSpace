using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Cost;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Sessions.Journal.FactsSources;

/// <summary>
/// Enriches each MODEL-CALL step with its structured facts (purpose · model · tokens · latency · cost · status), so the
/// expanded model-call fold shows a legible row instead of a bare "Model call · kind · N tokens" line — a model call is
/// the cost + intelligence source of an AI workflow, so it is SEEN, not downgraded. Reads the run's
/// <c>interaction.completed</c> / <c>interaction.failed</c> ledger records (the SAME ledger + id format
/// <c>record-{Sequence}</c> the <c>LifecycleStepDescriber</c> turns into the model-call step) and pairs each with its
/// <c>interaction.started</c> by correlation id for latency. Cost rides the SHARED <see cref="AgentCostPricing"/>, so a
/// per-call cost can't disagree with a run total. READ-ONLY, keyed by the completed record's step id.
/// </summary>
public sealed class ModelCallFactsSource : IJournalFactsSource
{
    private readonly CodeSpaceDbContext _db;

    public ModelCallFactsSource(CodeSpaceDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyDictionary<string, JournalStepFacts>> GatherAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var records = await _db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId && (r.RecordType == WorkflowRunRecordTypes.InteractionStarted
                || r.RecordType == WorkflowRunRecordTypes.InteractionCompleted
                || r.RecordType == WorkflowRunRecordTypes.InteractionFailed))
            .OrderBy(r => r.Sequence)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // Pair each completion with its START by correlation id — the start carries the earlier timestamp (latency) and,
        // later, the prompt (M3). First-wins on a duplicate id (defensive; a correlation id is one interaction).
        var startByCorrelation = records
            .Where(r => r.RecordType == WorkflowRunRecordTypes.InteractionStarted && r.CorrelationId is not null)
            .GroupBy(r => r.CorrelationId!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        var facts = new Dictionary<string, JournalStepFacts>();

        foreach (var completed in records.Where(r => r.RecordType is WorkflowRunRecordTypes.InteractionCompleted or WorkflowRunRecordTypes.InteractionFailed))
        {
            var start = completed.CorrelationId is { } cid && startByCorrelation.TryGetValue(cid, out var s) ? s : null;

            facts[$"record-{completed.Sequence}"] = new JournalStepFacts { ModelCall = From(completed, start) };
        }

        return facts;
    }

    /// <summary>Pure map of a completed/failed interaction record (+ its paired start) to the structured model-call facts. Latency is the start→completion span when paired; cost rides the shared pricing (fail-open null); tokens/model come off the completion payload. A failed record reads status "failed". Public-internal for unit pinning.</summary>
    internal static JournalModelCall From(WorkflowRunRecord completed, WorkflowRunRecord? start)
    {
        var kind = ReadString(completed, "kind");
        var model = ReadString(completed, "model");
        var (input, output) = ReadUsage(completed);
        var tokens = input is null && output is null ? (int?)null : (input ?? 0) + (output ?? 0);

        var latency = start is null ? (long?)null : (long)(completed.OccurredAt - start.OccurredAt).TotalMilliseconds;
        var status = completed.RecordType == WorkflowRunRecordTypes.InteractionFailed ? "failed" : "completed";

        return new JournalModelCall
        {
            Purpose = string.IsNullOrWhiteSpace(kind) ? "model call" : kind!,
            Model = model,
            InputTokens = input,
            OutputTokens = output,
            Tokens = tokens,
            LatencyMs = latency is >= 0 ? latency : null,
            CostUsd = string.IsNullOrWhiteSpace(model) || tokens is null ? null : AgentCostPricing.CostUsd(model, input ?? 0, output ?? 0),
            Status = status,
        };
    }

    /// <summary>Input + output tokens off the completion's nested <c>usage</c>, each null when absent (so a usage-silent call reads unknown, not a bogus zero).</summary>
    private static (int? Input, int? Output) ReadUsage(WorkflowRunRecord r)
    {
        try
        {
            using var doc = JsonDocument.Parse(r.PayloadJson);

            if (!doc.RootElement.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return (null, null);

            return (ReadInt(usage, "inputTokens"), ReadInt(usage, "outputTokens"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string? ReadString(WorkflowRunRecord r, string prop)
    {
        try
        {
            using var doc = JsonDocument.Parse(r.PayloadJson);

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
