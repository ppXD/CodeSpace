using System.Text.Json;

namespace CodeSpace.Core.Services.Tasks.Effort.Classifiers.Llm;

/// <summary>
/// The structured-LLM effort classifier's COMMIT-CONTRACT: the JSON Schema the model is constrained to and the
/// matching deserialization options (Rule 18 — co-located with the classifier; pinned by a unit test so a drift is a
/// reviewer-visible contract change). The model emits the SAME generic <c>EffortSignals</c> the heuristic derives —
/// observable properties of the work, NOT a task taxonomy — plus a CONFIDENCE the router gates the confirm card on. The
/// policy (<c>EffortPolicy</c>) still decides the tier from the signals, so the LLM only supplies better data, never the
/// routing logic.
/// </summary>
public static class LlmEffortClassifierSchema
{
    /// <summary>The root object the structured-LLM call is constrained to. A <see cref="LlmEffortClassification"/> round-trips from any conforming object.</summary>
    public static readonly JsonElement ResponseSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "needsCodeChange": { "type": "boolean", "description": "The task likely EDITS code (vs a pure read / analysis / question)." },
            "crossFile": { "type": "boolean", "description": "The work likely spans MULTIPLE files / modules rather than one localized edit." },
            "needsTestsOrCi": { "type": "boolean", "description": "The task likely needs tests written / run or CI to pass before it is done." },
            "ambiguous": { "type": "boolean", "description": "The goal is under-specified / open-ended enough that a human confirm or plan review is warranted." },
            "riskySideEffects": { "type": "boolean", "description": "The task may produce risky / irreversible side effects (delete, drop, migrate, deploy, production, secrets)." },
            "estimatedCostTier": { "type": "string", "enum": ["low", "medium", "high"], "description": "A rough complexity / cost tier for the work." },
            "confidence": { "type": "number", "description": "Your confidence in this classification, 0..1. >= 0.6 means route AUTOMATICALLY without a human confirm; below 0.6 the task is ambiguous and the operator is asked to confirm the effort." },
            "rationale": { "type": "string", "description": "One short line: why this effort, for the confirm card / observability." }
          },
          "required": ["needsCodeChange", "crossFile", "needsTestsOrCi", "ambiguous", "riskySideEffects", "estimatedCostTier", "confidence"]
        }
        """).RootElement.Clone();

    /// <summary>Deserialization options for mapping a schema-valid object into <see cref="LlmEffortClassification"/>. Case-insensitive so the model's lower-camel keys bind to the record's Pascal properties.</summary>
    public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
}

/// <summary>The deserialized structured-LLM reply (Rule 18.1 — a pure data noun). Maps onto <c>EffortSignals</c> + the router's confidence; the classifier clamps + normalizes before building the <c>EffortDecision</c>.</summary>
public sealed record LlmEffortClassification
{
    public bool NeedsCodeChange { get; init; }
    public bool CrossFile { get; init; }
    public bool NeedsTestsOrCi { get; init; }
    public bool Ambiguous { get; init; }
    public bool RiskySideEffects { get; init; }
    public string EstimatedCostTier { get; init; } = "low";
    public double Confidence { get; init; }
    public string Rationale { get; init; } = "";
}
