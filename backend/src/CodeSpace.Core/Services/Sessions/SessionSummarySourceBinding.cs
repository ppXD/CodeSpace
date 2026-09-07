using System.Text.Json;
using CodeSpace.Core.Services.Agents;

namespace CodeSpace.Core.Services.Sessions;

/// <summary>
/// One already-folded turn's SOURCE identity at fold time — <c>WorkSession.SummarySourceBindingJson</c> is a JSON
/// array of these, persisted alongside the rolling <c>WorkSession.Summary</c> prose. It is the durable pointer BACK
/// to the evidence a fold consumed — never re-derived from the model's own prose (a summary never determines settled
/// status) — so a resume window can recover an out-of-window turn's unresolved contract, and
/// <see cref="SessionSummarizer"/> can detect an effective-source change BEHIND an unmoved watermark.
/// <see cref="SessionSummarizer"/> is the sole writer; <see cref="SessionContextBuilder"/> reads it to carry a
/// folded turn's unresolved contract forward without re-scanning the whole thread's history.
/// </summary>
internal sealed record SessionSummarySourceBinding
{
    /// <summary>The top-level turn this entry binds (matches <c>WorkflowRun.SessionTurnIndex</c>).</summary>
    public required int Turn { get; init; }

    /// <summary>The turn's EFFECTIVE attempt id (<see cref="SessionTurnAttempts"/>) at the time this entry was written.</summary>
    public required Guid EffectiveRunId { get; init; }

    /// <summary>SHA-256 (hex) of the effective attempt's status + goal/result + its RESOLVED branch (the same manifest-preferred branch <c>SessionSummarizer.BuildUserPrompt</c> folds, never the raw <c>LegacyBranch</c> column alone) — changes whenever the folded content would change, without persisting the content itself.</summary>
    public required string ResultFingerprint { get; init; }

    /// <summary>The effective run's latest <c>CompletionAssessmentRecord.Id</c> at fold time, or null when none existed yet.</summary>
    public Guid? AssessmentId { get; init; }
}

/// <summary>Parse/serialize helpers for <c>WorkSession.SummarySourceBindingJson</c> — malformed or absent JSON reads as empty (a legacy row from before this column existed), never an error.</summary>
internal static class SessionSummarySourceBindings
{
    internal static IReadOnlyList<SessionSummarySourceBinding> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            var parsed = JsonSerializer.Deserialize<List<SessionSummarySourceBinding>>(json, AgentJson.Options);

            // Past JsonSerializer's own (JsonException-only) guarantee: a malformed `[null, ...]` element deserializes
            // WITHOUT throwing (there is no `required`-member check against a JSON null), and a hand-edited duplicate
            // "turn" key would throw INSIDE a caller's ToDictionary(b => b.Turn) instead of here — both must still
            // honor this class's "never an error" contract.
            return parsed?.Where(b => b is not null).DistinctBy(b => b.Turn).ToList() ?? [];
        }
        catch (JsonException) { return []; }
    }

    internal static string Serialize(IReadOnlyList<SessionSummarySourceBinding> bindings) => JsonSerializer.Serialize(bindings, AgentJson.Options);
}
