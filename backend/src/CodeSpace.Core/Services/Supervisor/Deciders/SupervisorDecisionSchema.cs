using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeSpace.Core.Services.Supervisor.Deciders;

/// <summary>
/// The supervisor decider's COMMIT-CONTRACT (PR-E E3): the JSON Schema the model is constrained to via the
/// structured-output path, and the matching deserialization options. Co-located with the decider concern
/// (Rule 18) and pinned by a unit test — a drift in the schema is a contract change a reviewer must see, not
/// an invisible refactor. Mirrors <c>PlannerSchema.ResponseSchema</c>.
///
/// <para>The model picks ONE <c>kind</c> from the seven-verb vocabulary (the resolver loop #379 added <c>resolve</c>,
/// which intentionally carries NO payload sub-object — the server assembles the resolver task deterministically from
/// durable data, so the model only picks the verb) and supplies that verb's bounded
/// payload (the schema documents each verb's fields). Per-verb payloads are bounded (subtasks [1..20],
/// spawn/merge subtask-id lists [1..20]) so a decision never fans out an unbounded wave. The schema is
/// <c>additionalProperties: false</c> at the root + every payload object, and — CRITICALLY — carries NO
/// node-id / type-key / workflow-id / run-id field. The model NEVER addresses graph topology: it emits a
/// decision the SERVER turns into a side effect (the ledger key, the agent-run waits, the node id are all
/// server-derived). A unit test pins this "no graph-ref" guard.</para>
/// </summary>
public static class SupervisorDecisionSchema
{
    /// <summary>The JSON-Schema (root object) the structured-LLM call is constrained to. A <c>SupervisorModelDecision</c> round-trips from any object that conforms.</summary>
    public static readonly JsonElement ResponseSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "kind": {
              "type": "string",
              "enum": ["plan", "spawn", "retry", "ask_human", "merge", "resolve", "stop"],
              "description": "The single next action. 'plan' decomposes the goal; 'spawn' fans out agents over planned subtasks; 'retry' re-runs one subtask; 'merge' synthesizes prior agent results; 'resolve' spawns ONE agent to reconcile a CONFLICTED integration (choose it only after a merge reported INTEGRATION CONFLICTED — the server assembles the resolver task from the conflict); 'ask_human' asks a question; 'stop' ends the run."
            },
            "plan": {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "goal": { "type": "string", "description": "The restated top-level goal this plan addresses." },
                "subtasks": {
                  "type": "array",
                  "minItems": 1,
                  "maxItems": 20,
                  "items": {
                    "type": "object",
                    "additionalProperties": false,
                    "properties": {
                      "id": { "type": "string", "description": "Stable, plan-local id for the subtask (referenced later by spawn/retry)." },
                      "title": { "type": "string", "description": "Short human title." },
                      "instruction": { "type": "string", "description": "The concrete instruction a spawned agent executes." },
                      "dependsOn": { "type": "array", "items": { "type": "string" }, "description": "Optional plan-local subtask ids this subtask depends on — the build-graph edges the server validates as a DAG (absent → an independent unit)." },
                      "acceptance": {
                        "type": "object",
                        "additionalProperties": false,
                        "properties": {
                          "command": { "type": "array", "minItems": 1, "items": { "type": "string" }, "description": "For kind=TestsPass (default): an argv the server runs to OBJECTIVELY verify this subtask is done. For kind=ArtifactPresent: the list of repo-relative deliverable file paths that must exist." },
                          "kind": { "type": "string", "enum": ["TestsPass", "ArtifactPresent"], "description": "Which objective oracle verifies this — TestsPass (run the command argv, exit 0) for code, or ArtifactPresent (the command lists deliverable files that must exist) for research/analysis output. Omit for TestsPass." },
                          "description": { "type": "string", "description": "Optional human-readable description of the subtask's acceptance check." }
                        },
                        "required": ["command"],
                        "description": "Optional per-subtask acceptance — the unit's objective definition of done, graded on the unit's own branch."
                      }
                    },
                    "required": ["id", "title", "instruction"]
                  }
                },
                "phases": {
                  "type": "array",
                  "minItems": 1,
                  "maxItems": 20,
                  "items": {
                    "type": "object",
                    "additionalProperties": false,
                    "properties": {
                      "id": { "type": "string", "description": "Stable, plan-local id for the phase." },
                      "title": { "type": "string", "description": "Short human title for the phase (e.g. 'Investigate', 'Implement', 'Review')." },
                      "subtaskIds": { "type": "array", "items": { "type": "string" }, "description": "The plan-local subtask ids this phase groups." },
                      "acceptance": {
                        "type": "object",
                        "additionalProperties": false,
                        "properties": {
                          "command": { "type": "array", "minItems": 1, "items": { "type": "string" }, "description": "An argv the server runs to OBJECTIVELY verify this phase is done." },
                          "description": { "type": "string", "description": "Optional human-readable description of the phase's acceptance check." }
                        },
                        "required": ["command"],
                        "description": "Optional per-phase acceptance check (recorded + projected; enforcement is a follow-up)."
                      }
                    },
                    "required": ["id", "title"]
                  },
                  "description": "Optional semantic phases (L4) grouping subtasks into named, accepting stages — for a legible plan. Absent → the flat subtask plan."
                }
              },
              "required": ["goal", "subtasks"],
              "description": "Required when kind == 'plan'. Decompose into 'subtasks'; optionally group them into named 'phases'."
            },
            "spawn": {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "subtaskIds": {
                  "type": "array",
                  "minItems": 1,
                  "maxItems": 20,
                  "items": { "type": "string" },
                  "description": "Plan-local subtask ids (from a prior 'plan') to fan out as parallel agent runs."
                },
                "agents": {
                  "type": "array",
                  "minItems": 1,
                  "maxItems": 20,
                  "items": {
                    "type": "object",
                    "additionalProperties": false,
                    "properties": {
                      "subtaskId": { "type": "string", "description": "The plan-local subtask this agent runs (its fan-out key)." },
                      "role": { "type": "string", "description": "Optional semantic role for this agent (e.g. 'backend implementer', 'security reviewer') — descriptive context, never a privilege." },
                      "goalOverride": { "type": "string", "description": "Optional replacement instruction for this agent's subtask." },
                      "repositoryId": { "type": "string", "description": "Optional primary repository (must be one the operator already bound; clamped server-side)." },
                      "targetRepos": { "type": "array", "items": { "type": "object", "additionalProperties": false, "properties": { "repositoryId": { "type": "string" }, "alias": { "type": "string" }, "access": { "type": "string", "enum": ["read", "write"] } }, "required": ["repositoryId"] }, "description": "Optional related-repo subset for this agent — clamped to a subset of the operator's bound repos with no access upgrade." },
                      "harness": { "type": "string", "description": "Optional harness request (granted only if the operator allow-list permits)." },
                      "model": { "type": "string", "description": "Optional model request." },
                      "autonomyLevel": { "type": "string", "enum": ["confined", "standard", "trusted", "unleashed"], "description": "Optional autonomy request (one of the four tiers) — clamped to the run profile's ceiling, never raised past it." },
                      "agentDefinition": { "type": "string", "description": "Optional Agent persona for this agent — the SLUG of one of the team's personas listed in the capability catalog (e.g. 'security-reviewer'). Gives this agent that persona's specialist prompt/model/tools. Must be a slug the catalog lists (fail-closed otherwise). Omit to use the run-level profile persona." }
                    },
                    "required": ["subtaskId"]
                  },
                  "description": "Optional per-agent dispatch overrides (L4) — one per subtask the model wants to give a distinct role / repo subset / execution envelope. The model proposes; the server clamps every field. Absent → every agent inherits the run-level profile."
                }
              },
              "required": ["subtaskIds"],
              "description": "Required when kind == 'spawn'. Fan out over 'subtaskIds'; optionally author a per-agent 'agents[]' override keyed by subtaskId."
            },
            "retry": {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "subtaskId": { "type": "string", "description": "The plan-local subtask id to re-run as a fresh agent attempt." },
                "revisedInstruction": { "type": "string", "description": "Optional replacement instruction for the retried subtask." }
              },
              "required": ["subtaskId"],
              "description": "Required when kind == 'retry'."
            },
            "askHuman": {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "question": { "type": "string", "description": "The question to ask the human (parked for E4)." }
              },
              "required": ["question"],
              "description": "Required when kind == 'ask_human'."
            },
            "merge": {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "synthesisInstruction": { "type": "string", "description": "Optional instruction guiding the synthesis." }
              },
              "required": [],
              "description": "Optional when kind == 'merge'. Merge synthesizes ALL prior agent results; it takes no subset (a selective subtask subset returns with the richer LLM-synthesis slice)."
            },
            "stop": {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "outcome": { "type": "string", "description": "Terminal outcome label (e.g. 'completed', 'failed', 'abandoned')." },
                "summary": { "type": "string", "description": "Short summary of what the supervisor accomplished." },
                "acceptance": {
                  "type": "object",
                  "additionalProperties": false,
                  "properties": {
                    "command": {
                      "type": "array",
                      "minItems": 1,
                      "items": { "type": "string" },
                      "description": "For kind=TestsPass (default): an argv the server runs against the produced workspace (non-zero exit = not accepted). For kind=ArtifactPresent: the repo-relative deliverable file paths that must exist. Authoring it makes 'done' a server-verified fact, not a self-report."
                    },
                    "kind": { "type": "string", "enum": ["TestsPass", "ArtifactPresent"], "description": "Which objective oracle verifies the result — TestsPass (run the command argv) for code, or ArtifactPresent (the command lists deliverable files that must exist) for research/analysis output. Omit for TestsPass. This tightening gate is AND-ed under the operator's own acceptance floor, which is always graded as TestsPass." },
                    "description": { "type": "string", "description": "Optional human-readable description of what the acceptance check proves." }
                  },
                  "required": ["command"],
                  "description": "Optional model-authored definition of done for the terminal result — a server-run acceptance check, AND-ed against the operator's own acceptance floor."
                }
              },
              "required": ["outcome", "summary"],
              "description": "Required when kind == 'stop'."
            }
          },
          "required": ["kind"]
        }
        """).RootElement.Clone();

    /// <summary>Deserialization options for mapping a schema-valid object back into <c>SupervisorModelDecision</c>. Case-insensitive so the model's lower-camel keys bind to the record's Pascal properties; the string-enum converter binds the acceptance <c>kind</c> ("TestsPass"/"ArtifactPresent") to <c>BenchmarkGradingKind</c>.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };
}
