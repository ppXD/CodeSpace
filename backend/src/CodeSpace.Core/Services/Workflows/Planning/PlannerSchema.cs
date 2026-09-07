using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeSpace.Core.Services.Workflows.Planning;

/// <summary>
/// The planner's COMMIT-CONTRACT: the JSON Schema the model is constrained to (via the structured-output
/// path) and the matching deserialization options. Co-located with the planner concern (Rule 18) and
/// pinned by a unit test — a drift in either the schema or the property mapping is a contract change a
/// reviewer must see, not an invisible refactor.
///
/// <para>The schema's property names map onto the model-authored projection of <c>PlannedWorkflow</c> /
/// <c>PlannedSubtask</c>, with a versioned <see cref="PlannerAcceptanceDraft"/> mapped explicitly into the existing
/// runtime acceptance contract. Server-stamped provenance remains outside model input. The options are case-insensitive so the model's <c>goal</c> binds to
/// <c>Goal</c>. <c>additionalProperties: false</c> + <c>required</c> keep the model from inventing fields or dropping
/// the ones the projector depends on; <c>subtasks</c> is bounded <c>[1,20]</c>.</para>
/// </summary>
public static class PlannerSchema
{
    /// <summary>The JSON schema constraining fresh model output. PlannerAcceptanceDraft maps its typed acceptance payloads before the normalized PlannedWorkflow reaches persistence or execution.</summary>
    public static readonly JsonElement ResponseSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "goal": { "type": "string", "description": "The restated top-level goal the plan addresses." },
            "subtasks": {
              "type": "array",
              "minItems": 1,
              "maxItems": 20,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "id": { "type": "string", "description": "Stable, plan-local id for the subtask." },
                  "title": { "type": "string", "description": "Short human title." },
                  "instruction": { "type": "string", "description": "The concrete instruction the branch executes." },
                  "rationale": { "type": "string", "description": "Optional one-line 'why this subtask'." },
                  "harness": { "type": "string", "description": "Optional — the best harness for THIS subtask, chosen from the capability catalog (one whose providers can drive your chosen model). Omit to use the run default." },
                  "model": { "type": "string", "description": "Optional — the best model for THIS subtask, chosen from the run's credentialed pool in the capability catalog (match its provider to the chosen harness). Omit for the harness default." },
                  "kind": { "type": "string", "description": "Optional short open kind for this subtask — e.g. research / code / analysis / write. A hint for allocation + rendering; omit when unsure." },
                  "dependsOn": { "type": "array", "items": { "type": "string" }, "description": "Optional plan-local subtask ids this subtask depends on — the plan's DAG edges (absent → an independent unit)." },
                  "acceptanceCriteria": { "type": "array", "items": { "type": "string" }, "description": "Optional short SUBJECTIVE completion qualities a reviewer checks (never executed) — e.g. 'covers edge cases', 'cites sources'." },
                  "acceptance": {
                    "type": "object",
                    "additionalProperties": false,
                    "properties": {
                      "formatVersion": { "type": "integer", "enum": [2], "description": "Typed acceptance wire version. Always 2; legacy command payloads are not accepted from a fresh planner response." },
                      "argv": { "type": "array", "minItems": 1, "items": { "type": "string" }, "description": "Required only for TestsPass. Exact executable followed by its individual arguments; preserve empty and whitespace arguments. Never shell-split or put file obligations here. Omit artifactPaths." },
                      "oraclePaths": { "type": "array", "items": { "type": "string" }, "description": "Optional exact relative oracle file paths already present before execution in a local repository-free workspace. The server hashes these explicit files before the agent runs and rejects any changed bytes; no symlinks, globs, git pathspecs or inferred shell dependencies. These are judge inputs, not output artifact obligations." },
                      "artifactPaths": { "type": "array", "minItems": 1, "items": { "type": "string" }, "description": "Required for ArtifactPresent, LlmJudge, CitationsResolve or ArtifactSchema. Workspace-relative deliverable file paths that this oracle reads. These are literal paths, not executable names, shell commands or argv. Omit argv." },
                      "kind": { "type": "string", "enum": ["TestsPass", "ArtifactPresent", "LlmJudge", "CitationsResolve", "ArtifactSchema"], "description": "Required oracle choice based on the evidence needed, independently of task type. TestsPass executes argv and requires exit 0; ArtifactPresent checks file existence; LlmJudge evaluates file content against rubric; CitationsResolve checks citations; ArtifactSchema validates file content against schema. File existence cannot replace a required behavioral or content check." },
                      "rubric": { "type": "object", "additionalProperties": false, "properties": { "criteria": { "type": "array", "minItems": 1, "items": { "type": "object", "additionalProperties": false, "properties": { "id": { "type": "string" }, "requirement": { "type": "string", "description": "A concrete requirement judgeable on the deliverable alone, e.g. 'names at least three competitors with sources'." }, "weight": { "type": "number", "description": "Relative weight (omit for 1)." } }, "required": ["id", "requirement"] } }, "threshold": { "type": "number", "description": "Weighted met-fraction (0..1] required to pass. Omit for 1.0 — every criterion." } }, "required": ["criteria"], "description": "REQUIRED for kind=LlmJudge: the weighted BINARY criteria an independent judge grades the deliverables against." },
                      "schema": { "type": "object", "additionalProperties": true, "description": "REQUIRED for kind=ArtifactSchema: the JSON schema each deliverable file must validate against (required / type / enum / nested properties+items)." },
                      "description": { "type": "string", "description": "Optional human-readable description of the subtask's acceptance check." }
                    },
                    "required": ["formatVersion", "kind"],
                    "oneOf": [
                      {
                        "title": "TestsPass acceptance",
                        "properties": {
                          "formatVersion": { "type": "integer", "enum": [2] },
                          "kind": { "type": "string", "enum": ["TestsPass"] },
                          "argv": { "type": "array", "minItems": 1, "items": { "type": "string" } }
                        },
                        "required": ["formatVersion", "kind", "argv"],
                        "not": { "required": ["artifactPaths"] }
                      },
                      {
                        "title": "ArtifactPresent acceptance",
                        "properties": {
                          "formatVersion": { "type": "integer", "enum": [2] },
                          "kind": { "type": "string", "enum": ["ArtifactPresent"] },
                          "artifactPaths": { "type": "array", "minItems": 1, "items": { "type": "string" } }
                        },
                        "required": ["formatVersion", "kind", "artifactPaths"],
                        "not": { "required": ["argv"] }
                      },
                      {
                        "title": "CitationsResolve acceptance",
                        "properties": {
                          "formatVersion": { "type": "integer", "enum": [2] },
                          "kind": { "type": "string", "enum": ["CitationsResolve"] },
                          "artifactPaths": { "type": "array", "minItems": 1, "items": { "type": "string" } }
                        },
                        "required": ["formatVersion", "kind", "artifactPaths"],
                        "not": { "required": ["argv"] }
                      },
                      {
                        "title": "LlmJudge acceptance",
                        "properties": {
                          "formatVersion": { "type": "integer", "enum": [2] },
                          "kind": { "type": "string", "enum": ["LlmJudge"] },
                          "artifactPaths": { "type": "array", "minItems": 1, "items": { "type": "string" } },
                          "rubric": { "type": "object", "properties": { "criteria": { "type": "array", "minItems": 1 } }, "required": ["criteria"] }
                        },
                        "required": ["formatVersion", "kind", "artifactPaths", "rubric"],
                        "not": { "required": ["argv"] }
                      },
                      {
                        "title": "ArtifactSchema acceptance",
                        "properties": {
                          "formatVersion": { "type": "integer", "enum": [2] },
                          "kind": { "type": "string", "enum": ["ArtifactSchema"] },
                          "artifactPaths": { "type": "array", "minItems": 1, "items": { "type": "string" } },
                          "schema": { "type": "object" }
                        },
                        "required": ["formatVersion", "kind", "artifactPaths", "schema"],
                        "not": { "required": ["argv"] }
                      }
                    ],
                    "description": "Optional per-subtask acceptance — the unit's objective definition of done, authored WITH the task so the evaluation layer grades against the plan's own contract."
                  }
                },
                "required": ["id", "title", "instruction"]
              }
            },
            "successCriteria": { "type": "array", "items": { "type": "string" }, "description": "Observable conditions that, together, mean the goal is done." },
            "risks": { "type": "array", "items": { "type": "string" }, "description": "Risks / unknowns the plan carries." },
            "recommendedWorkflowKind": { "type": "string", "description": "Execution shape per branch: 'coding' projects onto an agent; anything else onto an LLM step." },
            "hasEnoughContext": { "type": "boolean", "description": "true ONLY when the goal needs no execution at all — the plan itself already answers it. Be very strict: even at 90% certainty, prefer execution (false)." },
            "assumptions": { "type": "array", "items": { "type": "string" }, "description": "Defaults you chose where the goal was ambiguous — record them so the reviewer sees what was assumed." },
            "questions": {
              "type": "array",
              "maxItems": 3,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "id": { "type": "string", "description": "Stable, plan-local id for the question." },
                  "question": { "type": "string", "description": "The decision the operator should make." },
                  "options": {
                    "type": "array",
                    "minItems": 2,
                    "maxItems": 4,
                    "items": {
                      "type": "object",
                      "additionalProperties": false,
                      "properties": {
                        "id": { "type": "string", "description": "Stable, question-local id." },
                        "label": { "type": "string", "description": "The operator-facing option label." }
                      },
                      "required": ["id", "label"]
                    }
                  },
                  "recommendedOptionId": { "type": "string", "description": "The option to proceed with when the operator doesn't answer — also record it under assumptions." },
                  "allowFreeText": { "type": "boolean", "description": "Whether a free-text answer is also acceptable." }
                },
                "required": ["id", "question", "options"]
              },
              "description": "Optional operator questions (mutually exclusive options + a recommended default) when the goal leaves a real direction choice open. Omit when the plan needs no operator input."
            }
          },
          "required": ["goal", "subtasks"]
        }
        """).RootElement.Clone();

    /// <summary>Deserialization options for mapping a schema-valid object back into <c>PlannedWorkflow</c>. Case-insensitive so the model's lower-camel keys bind to the record's Pascal properties; the string-enum converter binds the acceptance <c>kind</c> ("TestsPass"/"ArtifactPresent") to <c>BenchmarkGradingKind</c>.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
}
