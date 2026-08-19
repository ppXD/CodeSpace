using System.Text.Json;

namespace CodeSpace.Core.Services.Learning;

/// <summary>
/// The structured-output contract for the nightly post-mortem call (mirrors <c>ModelTieringSchema</c>): the brain
/// is handed yesterday's failed/parked runs (error + decision tape) plus the team's CURRENT lessons, and returns
/// consolidation operations — the Mem0 op set: <c>add</c> a new lesson, <c>update</c> an existing one (citations
/// merge), <c>invalidate</c> a contradicted one (temporal, one-way), or <c>noop</c>. Every add/update must cite
/// runs the prompt actually showed — the fold rejects anything else (anti-confabulation).
/// </summary>
public static class LessonDistillationSchema
{
    public static readonly JsonElement ResponseSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "lessons": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "action": { "type": "string", "enum": ["add", "update", "invalidate", "noop"], "description": "add a new lesson; update/invalidate an existing one by id; noop when a run teaches nothing new." },
                  "existingLessonId": { "type": ["string", "null"], "description": "Required for update/invalidate — the id of a CURRENT lesson from the list you were shown." },
                  "failureClass": { "type": "string", "description": "Short kebab-case class of the failure, e.g. broken-acceptance-command." },
                  "whatFailed": { "type": "string" },
                  "why": { "type": "string" },
                  "howToApply": { "type": "string", "description": "What the planner should do differently next time — imperative, concrete." },
                  "sourceRunIds": { "type": "array", "items": { "type": "string" }, "description": "The run ids (from the prompt) that teach this lesson. Never cite a run you were not shown." }
                },
                "required": ["action", "failureClass", "whatFailed", "why", "howToApply", "sourceRunIds"]
              }
            }
          },
          "required": ["lessons"]
        }
        """).RootElement.Clone();

    /// <summary>Case-insensitive so the model's lower-camel keys bind; unknown enum-ish strings are handled by the fold, never thrown on.</summary>
    public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
}

/// <summary>The distillation reply (a data noun co-located with its schema, tiering precedent).</summary>
public sealed record LessonProposals
{
    public List<LessonProposal> Lessons { get; init; } = [];
}

public sealed record LessonProposal
{
    public string? Action { get; init; }
    public string? ExistingLessonId { get; init; }
    public string? FailureClass { get; init; }
    public string? WhatFailed { get; init; }
    public string? Why { get; init; }
    public string? HowToApply { get; init; }
    public List<string> SourceRunIds { get; init; } = [];
}
