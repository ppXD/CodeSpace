using System.Text.Json;

namespace CodeSpace.Core.Services.Tasks.SpecPreview;

/// <summary>
/// The spec compiler's COMMIT-CONTRACT (the <see cref="Effort.Classifiers.Llm.LlmEffortClassifierSchema"/>
/// pattern): the JSON Schema the model is constrained to and the matching deserialization options, pinned by a
/// unit test so a drift is a reviewer-visible contract change. The model emits SUGGESTIONS for the launch
/// surface's existing fields — never authority, never anything the launch cannot carry.
/// </summary>
public static class TaskSpecCompilerSchema
{
    /// <summary>The root object the structured call is constrained to. A <see cref="TaskSpecCompilation"/> round-trips from any conforming object.</summary>
    public static readonly JsonElement ResponseSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "acceptanceChecks": { "type": "array", "items": { "type": "string" }, "description": "An EXECUTABLE check as argv tokens (e.g. [\"dotnet\", \"test\"] or [\"sh\", \"-c\", \"npm test\"]) that objectively verifies the goal is done. Suggest one ONLY when the repository layout shows the toolchain actually exists (a test project, a package.json with a test script, a Makefile target). EMPTY when unsure — a wrong check is worse than none: it fails correct work forever." },
            "acceptanceCriteria": { "type": "array", "items": { "type": "string" }, "description": "Crisp definition-of-done bullets a reviewer could verify (behavioral outcomes, not restatements of the goal). Empty when the goal is already a precise single criterion." },
            "openPullRequest": { "type": "boolean", "description": "true when the goal implies the change should arrive as a pull request; false when it explicitly should not; use false only for an explicit don't." },
            "hasDeliveryOpinion": { "type": "boolean", "description": "Whether the goal expresses ANY delivery opinion at all. false = ignore openPullRequest entirely (no opinion is the common case and must never be invented)." },
            "targetBranch": { "type": "string", "description": "The PR target branch, ONLY when the goal names one explicitly (e.g. 'against release/2.0'). Empty string when the goal names none — never guess a branch." },
            "confidence": { "type": "number", "description": "Your confidence in this suggestion set, 0..1." },
            "rationale": { "type": "string", "description": "One short line: why these suggestions — shown on the suggestion card." }
          },
          "required": ["acceptanceChecks", "acceptanceCriteria", "hasDeliveryOpinion", "openPullRequest", "confidence", "rationale"]
        }
        """).RootElement.Clone();

    /// <summary>Deserialization options for mapping a schema-valid object into <see cref="TaskSpecCompilation"/>. Case-insensitive so the model's lower-camel keys bind to the record's Pascal properties.</summary>
    public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
}

/// <summary>The deserialized structured reply (Rule 18.1 — a pure data noun). The compiler validates + normalizes before anything reaches the caller.</summary>
public sealed record TaskSpecCompilation
{
    public IReadOnlyList<string> AcceptanceChecks { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AcceptanceCriteria { get; init; } = Array.Empty<string>();
    public bool HasDeliveryOpinion { get; init; }
    public bool OpenPullRequest { get; init; }
    public string TargetBranch { get; init; } = "";
    public double Confidence { get; init; }
    public string Rationale { get; init; } = "";
}
