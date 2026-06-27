using System.Text.Json;

namespace CodeSpace.Core.Services.Review;

/// <summary>
/// The critic's COMMIT-CONTRACTS — the JSON Schemas the reviewer model is constrained to, one per mode (GATE = approve +
/// score + issues; IMPROVE = a critique to fold back), plus the matching deserialization options. Co-located with the
/// critic (Rule 18) and pinned by a unit test (a schema drift is a visible contract change). <c>additionalProperties:false</c>.
/// </summary>
public static class CriticSchema
{
    public static readonly JsonElement GateSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "approved": { "type": "boolean", "description": "true ONLY if the artifact soundly achieves the goal with no material flaw; false if it has a real problem a human should weigh." },
            "score": { "type": "integer", "minimum": 0, "maximum": 100, "description": "An overall quality score 0-100." },
            "issues": { "type": "array", "items": { "type": "string" }, "description": "Concrete, specific problems found — empty if none." },
            "rationale": { "type": "string", "description": "Why you approved or flagged it — REQUIRED." }
          },
          "required": ["approved", "rationale"]
        }
        """).RootElement.Clone();

    public static readonly JsonElement ImproveSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "critique": { "type": "string", "description": "Specific, ACTIONABLE critique the author can use to revise the artifact — what is weak/missing/wrong and how to fix it. REQUIRED." },
            "issues": { "type": "array", "items": { "type": "string" }, "description": "The concrete problems, itemised — empty if none." },
            "rationale": { "type": "string", "description": "A short summary of your overall assessment — REQUIRED." }
          },
          "required": ["critique", "rationale"]
        }
        """).RootElement.Clone();

    /// <summary>Case-insensitive so the model's lower-camel keys bind.</summary>
    public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
}

/// <summary>The GATE reviewer's raw schema-valid output, before projection.</summary>
internal sealed record GateModelReview
{
    public bool Approved { get; init; }
    public int? Score { get; init; }
    public IReadOnlyList<string>? Issues { get; init; }
    public string? Rationale { get; init; }
}

/// <summary>The IMPROVE reviewer's raw schema-valid output, before projection.</summary>
internal sealed record ImproveModelReview
{
    public string? Critique { get; init; }
    public IReadOnlyList<string>? Issues { get; init; }
    public string? Rationale { get; init; }
}
