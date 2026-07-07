using System.Text.Json;
using CodeSpace.Messages.Review;

namespace CodeSpace.Core.Services.Review;

/// <summary>
/// The critic's COMMIT-CONTRACTS — the JSON Schemas the reviewer model is constrained to, one per mode (GATE = approve +
/// score + issues; IMPROVE = a critique to fold back), plus the matching deserialization options. Every issue carries
/// EVIDENCE (S8): a quote or precise location in the artifact that grounds it — what turns an opinion into an auditable,
/// meta-evaluable verdict. Co-located with the critic (Rule 18) and pinned by a unit test (a schema drift is a visible
/// contract change). <c>additionalProperties:false</c>.
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
            "issues": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "issue": { "type": "string", "description": "One concrete, specific problem." },
                  "evidence": { "type": "string", "description": "The grounding — QUOTE the offending part of the artifact or name its precise location. REQUIRED: an unevidenced issue is an opinion, not a finding." },
                  "severity": { "type": "string", "enum": ["blocker", "major", "minor"], "description": "How badly this undermines the goal. blocker = makes the artifact UNFIT (wrong/broken/unsafe/incomplete, or fails a hard requirement) — the ONLY severity that halts. major = a real problem worth fixing that does NOT make it unfit. minor = a nitpick/style preference. REQUIRED." }
                },
                "required": ["issue", "evidence", "severity"]
              },
              "description": "Concrete, specific problems found, each with evidence AND a severity — empty if none."
            },
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
            "issues": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "issue": { "type": "string", "description": "One concrete, specific problem." },
                  "evidence": { "type": "string", "description": "The grounding — QUOTE the offending part of the artifact or name its precise location. REQUIRED." },
                  "severity": { "type": "string", "enum": ["blocker", "major", "minor"], "description": "How badly this undermines the goal. blocker = makes the artifact UNFIT. major = a real problem worth fixing that does NOT make it unfit. minor = a nitpick/style preference (revising against minor-only issues is not worth a round). REQUIRED." }
                },
                "required": ["issue", "evidence", "severity"]
              },
              "description": "The concrete problems, itemised with evidence AND a severity — empty if none."
            },
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
    public IReadOnlyList<ModelIssue>? Issues { get; init; }
    public string? Rationale { get; init; }
}

/// <summary>The IMPROVE reviewer's raw schema-valid output, before projection.</summary>
internal sealed record ImproveModelReview
{
    public string? Critique { get; init; }
    public IReadOnlyList<ModelIssue>? Issues { get; init; }
    public string? Rationale { get; init; }
}

/// <summary>One raw issue as the model returns it ({issue, evidence, severity}), before projection onto <see cref="CriticIssue"/>.</summary>
internal sealed record ModelIssue
{
    public string? Issue { get; init; }
    public string? Evidence { get; init; }
    public string? Severity { get; init; }
}

/// <summary>The shared projection: raw model issues → canonical evidence-attached, SEVERITY-graded <see cref="CriticIssue"/>s (blank-text entries dropped; missing evidence degrades to empty, an absent/unknown severity degrades to <see cref="CriticSeverity.Major"/> — never a silent blocker, never a silent nitpick).</summary>
internal static class ModelIssueProjection
{
    public static IReadOnlyList<CriticIssue> Project(IReadOnlyList<ModelIssue>? issues) =>
        issues is not { Count: > 0 }
            ? Array.Empty<CriticIssue>()
            : issues.Where(i => !string.IsNullOrWhiteSpace(i.Issue))
                .Select(i => new CriticIssue { Text = i.Issue!.Trim(), Evidence = i.Evidence?.Trim() ?? "", Severity = ParseSeverity(i.Severity) })
                .ToList();

    /// <summary>Parse the model's severity token case-insensitively; an absent / unknown value degrades to <see cref="CriticSeverity.Major"/> (a real-but-non-fatal concern — the safe middle, never a silent blocker or a silent nitpick).</summary>
    internal static CriticSeverity ParseSeverity(string? raw) =>
        Enum.TryParse<CriticSeverity>(raw?.Trim(), ignoreCase: true, out var severity) ? severity : CriticSeverity.Major;
}
