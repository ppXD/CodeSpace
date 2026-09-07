using System.Text.Json;

namespace CodeSpace.Core.Services.Tasks.SpecPreview;

/// <summary>
/// The spec compiler's COMMIT-CONTRACT (the <see cref="Effort.Classifiers.Llm.LlmEffortClassifierSchema"/>
/// pattern): the JSON Schema the model is constrained to and the matching deserialization options, pinned by a
/// unit test so a drift is a reviewer-visible contract change. The model emits SUGGESTIONS for the launch
/// surface with preview evidence and dependency proposals. Model output never supplies authority or an execution receipt.
/// </summary>
public static class TaskSpecCompilerSchema
{
    /// <summary>The root object the structured call is constrained to. A <see cref="TaskSpecCompilation"/> round-trips from any conforming object.</summary>
    public static readonly JsonElement ResponseSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "acceptanceChecks": { "type": "array", "items": { "type": "string" }, "description": "One PROPOSED executable check as exact argv tokens. Preserve whitespace inside arguments. Prefer a command explicitly requested by the user or defined by observed repository evidence. EMPTY when unsure; a candidate may instead require evidence or dependency verification and will not become a mandatory check without source assessment." },
            "acceptanceArgvSource": { "type": "object", "additionalProperties": false, "properties": { "sourceId": { "type": "string" }, "quote": { "type": "string", "description": "Exact complete JSON argv array excerpt from the selected source, including every empty argument. The server verifies the excerpt and decodes it; do not regenerate its tokens in acceptanceChecks." }, "encoding": { "type": "string", "enum": ["json-argv"] } }, "required": ["sourceId", "quote", "encoding"], "description": "Optional literal reference when a supplied source gives an exact JSON argv array. Leave acceptanceChecks empty when using this reference. This verifies token fidelity only; an independent semantic review still determines whether the command is requested and relevant." },
            "evidencePaths": { "type": "array", "items": { "type": "string" }, "description": "Up to four relative repository files whose contents would establish the candidate command and its dependencies. Choose from the task and observed layout, without assuming a toolchain from the problem description. The server will read these files at the observed commit; unavailable files remain unknown." },
            "dependencies": { "type": "array", "items": { "type": "object", "additionalProperties": false, "properties": { "requirement": { "type": "string" }, "validationStrategy": { "type": "string" } }, "required": ["requirement", "validationStrategy"] }, "description": "Prerequisites the candidate relies on, with a concrete proposed strategy to verify each. These are plans, not claims that a tool or check has already run. General document and research tasks may have content criteria instead of an executable command." },
            "acceptanceCriteria": { "type": "array", "items": { "type": "string" }, "description": "Crisp definition-of-done bullets a reviewer could verify (behavioral outcomes, not restatements of the goal). Empty when the goal is already a precise single criterion." },
            "openPullRequest": { "type": "boolean", "description": "true when the goal implies the change should arrive as a pull request; false when it explicitly should not; use false only for an explicit don't." },
            "hasDeliveryOpinion": { "type": "boolean", "description": "Whether the goal expresses ANY delivery opinion at all. false = ignore openPullRequest entirely (no opinion is the common case and must never be invented)." },
            "targetBranch": { "type": "string", "description": "The PR target branch, ONLY when the goal names one explicitly (e.g. 'against release/2.0'). Empty string when the goal names none — never guess a branch." },
            "confidence": { "type": "number", "description": "Your confidence in this suggestion set, 0..1." },
            "rationale": { "type": "string", "description": "One short line: why these suggestions — shown on the suggestion card." }
          },
          "required": ["acceptanceChecks", "evidencePaths", "dependencies", "acceptanceCriteria", "hasDeliveryOpinion", "openPullRequest", "confidence", "rationale"]
        }
        """).RootElement.Clone();

    /// <summary>Deserialization options for mapping a schema-valid object into <see cref="TaskSpecCompilation"/>. Case-insensitive so the model's lower-camel keys bind to the record's Pascal properties.</summary>
    public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static readonly JsonElement ReviewSchema = JsonDocument.Parse("""
        {
          "type": "object", "additionalProperties": false,
          "properties": {
            "source": { "type": "string", "enum": ["user-explicit", "repository-evidence", "proposed-unverified"] },
            "support": { "type": "string", "enum": ["supported", "unknown", "contradicted"] },
            "dependenciesSupported": { "type": "boolean", "description": "Whether the observed file contents establish the command's declared repository prerequisites. A filename/listing alone is insufficient. This does not claim the command has been executed." },
            "citations": { "type": "array", "items": { "type": "object", "additionalProperties": false, "properties": { "sourceId": { "type": "string" }, "quote": { "type": "string" } }, "required": ["sourceId", "quote"] }, "description": "Exact excerpts from supplied source IDs, including the surrounding context that establishes intent. A matching quote is only source integrity: judge its meaning, negation and relevance independently." },
            "reason": { "type": "string", "description": "Explain the source assessment, counter-evidence and any uncertainty. No execution, verification or permission claim." }
          },
          "required": ["source", "support", "dependenciesSupported", "citations", "reason"]
        }
        """).RootElement.Clone();
}

/// <summary>The deserialized structured reply (Rule 18.1 — a pure data noun). The compiler validates + normalizes before anything reaches the caller.</summary>
public sealed record TaskSpecCompilation
{
    public IReadOnlyList<string> AcceptanceChecks { get; init; } = Array.Empty<string>();
    public TaskSpecArgvSource? AcceptanceArgvSource { get; init; }
    public IReadOnlyList<string> EvidencePaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<CodeSpace.Messages.Tasks.TaskSpecDependency> Dependencies { get; init; } = Array.Empty<CodeSpace.Messages.Tasks.TaskSpecDependency>();
    public IReadOnlyList<string> AcceptanceCriteria { get; init; } = Array.Empty<string>();
    public bool HasDeliveryOpinion { get; init; }
    public bool OpenPullRequest { get; init; }
    public string TargetBranch { get; init; } = "";
    public double Confidence { get; init; }
    public string Rationale { get; init; } = "";
}

public sealed record TaskSpecReview
{
    public string Source { get; init; } = "proposed-unverified";
    public string Support { get; init; } = "unknown";
    public bool DependenciesSupported { get; init; }
    public IReadOnlyList<TaskSpecReviewCitation> Citations { get; init; } = Array.Empty<TaskSpecReviewCitation>();
    public string Reason { get; init; } = "";
}

public sealed record TaskSpecReviewCitation(string SourceId, string Quote);

/// <summary>A model-selected literal source reference. Its bytes and interpretation are checked by the server; it does not establish user intent or execution authority.</summary>
public sealed record TaskSpecArgvSource(string SourceId, string Quote, string Encoding);
