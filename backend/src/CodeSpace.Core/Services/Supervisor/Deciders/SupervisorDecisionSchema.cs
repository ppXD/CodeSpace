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
/// payload (the schema documents each verb's fields). A single ROOT-LEVEL <c>rationale</c> (why + evidence) is
/// authored uniformly for EVERY verb — a decision-level annotation, not verb payload data — so the trace explains
/// WHY, not just what. Per-verb payloads are bounded (subtasks [1..20],
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
            "rationale": {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "why": { "type": "string", "description": "Why you are making THIS decision (one or two sentences) — the reasoning a reader needs to understand it. Applies to any kind: why this plan, why spawn these subtasks, why retry, why merge now, why stop." },
                "evidence": { "type": "string", "description": "The concrete evidence you acted on — the prior attempt's error / output / status / acceptance verdict that drove this decision." }
              },
              "description": "STRONGLY RECOMMENDED for every decision. A short structured rationale so the trace explains WHY you decided as you did — not just what you did. Especially valuable on a re-plan, a repeat retry, or a stop."
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
                      "expectsChanges": { "type": "boolean", "description": "Whether this subtask is expected to produce a code change (a diff). Set to false for a genuinely READ-ONLY subtask (investigation, analysis, review, a report — no file edits), so its acceptance check, if any, grades a vacuous PASS instead of failing for having no diff to verify. Omit (or true) for any subtask that should produce a change — the default." },
                      "acceptance": {
                        "type": "object",
                        "additionalProperties": false,
                        "properties": {
                          "command": { "type": "array", "minItems": 1, "items": { "type": "string" }, "description": "For kind=TestsPass (default): an argv the server runs to OBJECTIVELY verify this subtask is done. For every other kind: the repo-relative DELIVERABLE file paths the oracle reads (ArtifactPresent: must exist; LlmJudge: judged against the rubric; CitationsResolve: every citation must resolve; ArtifactSchema: must validate against the schema)." },
                          "kind": { "type": "string", "enum": ["TestsPass", "ArtifactPresent", "LlmJudge", "CitationsResolve", "ArtifactSchema"], "description": "Which objective oracle verifies this — TestsPass (run the argv, exit 0) for code; for research/analysis/data output: ArtifactPresent (deliverables exist), LlmJudge (an independent judge grades the deliverables against the rubric — author the rubric), CitationsResolve (every markdown citation in the deliverables resolves), ArtifactSchema (each deliverable validates against the schema — author the schema). Omit for TestsPass." },
                          "rubric": { "type": "object", "additionalProperties": false, "properties": { "criteria": { "type": "array", "minItems": 1, "items": { "type": "object", "additionalProperties": false, "properties": { "id": { "type": "string" }, "requirement": { "type": "string", "description": "A concrete requirement judgeable on the deliverable alone, e.g. 'names at least three competitors with sources'." }, "weight": { "type": "number", "description": "Relative weight (omit for 1)." } }, "required": ["id", "requirement"] } }, "threshold": { "type": "number", "description": "Weighted met-fraction (0..1] required to pass. Omit for 1.0 — every criterion." } }, "required": ["criteria"], "description": "REQUIRED for kind=LlmJudge: the weighted BINARY criteria an independent judge grades the deliverables against." },
                          "schema": { "type": "object", "additionalProperties": true, "description": "REQUIRED for kind=ArtifactSchema: the JSON schema each deliverable file must validate against (required / type / enum / nested properties+items)." },
                          "protectedPaths": { "type": "array", "items": { "type": "string" }, "description": "Repo-relative paths (git pathspecs) whose bytes belong to the ORACLE, not the worker — the test files, check scripts, and fixtures the acceptance command runs. The server restores these from the task's base before grading, so a worker's edits to its own judge are VOID (and recorded as tamper evidence). Name them whenever the acceptance command executes repo-resident files the worker could rewrite." },
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
                },
                "delivery": {
                  "type": "object",
                  "additionalProperties": false,
                  "properties": {
                    "openPullRequest": { "type": "boolean", "description": "Whether this run should automatically open a pull request once its accepted work is published. Propose true ONLY when the goal or the user's own instructions actually ask for it; omit otherwise." },
                    "targetBranch": { "type": "string", "description": "The branch a requested pull request should target. Omit to use the repository's own default branch." }
                  },
                  "description": "Optional delivery contract for what this run should produce beyond the code change itself. The operator's own configuration always overrides this per field when it names one."
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
                      "repositoryId": { "type": "string", "description": "Optional primary repository — the EXACT repository id (a uuid) as listed under 'Bound repositories' in the capability catalog, NEVER a name or alias (a non-uuid value is ignored and the run-level repository applies). Clamped server-side." },
                      "targetRepos": { "type": "array", "items": { "type": "object", "additionalProperties": false, "properties": { "repositoryId": { "type": "string", "description": "The EXACT repository id (a uuid) from the capability catalog — never a name or alias." }, "alias": { "type": "string" }, "access": { "type": "string", "enum": ["read", "write"] } }, "required": ["repositoryId"] }, "description": "Optional related-repo subset for this agent — clamped to a subset of the operator's bound repos with no access upgrade." },
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
              "description": "Required when kind == 'retry'. Author the top-level 'rationale' (why + evidence) so the trace explains WHY you retried — especially when retrying the SAME subtask more than once."
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
                "outcome": { "type": "string", "description": "Terminal outcome label (e.g. 'completed', 'failed', 'abandoned'). Use 'needs_clarification' when ONLY THE USER can unblock you (an ambiguous goal, a missing credential/decision) — state the exact question in summary. An honest ask is never punished as a failure; a guessed attempt that fails IS. Never use it to dodge work you could verify yourself." },
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

    /// <summary>Deserialization options for mapping a schema-valid object back into <c>SupervisorModelDecision</c>. Case-insensitive so the model's lower-camel keys bind to the record's Pascal properties; the string-enum converter binds the acceptance <c>kind</c> ("TestsPass"/"ArtifactPresent") to <c>BenchmarkGradingKind</c>; the lenient Guid converter absorbs a non-uuid PROPOSAL (a repo alias where an id belongs) as an absent field instead of killing the whole decision.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(), new LenientGuidConverter() },
    };
}

/// <summary>
/// A PROPOSAL-tolerant <see cref="Guid"/>? reader for the decider's bind step: the JSON schema types id fields as
/// plain strings (a JSON schema cannot enforce uuid-ness), so a model may propose a NAME where an id belongs — e.g.
/// <c>"repositoryId": "backend"</c> (the exact miss that killed a real run: schema-valid, bind-fatal). Every such
/// field is an OPTIONAL override the server clamps anyway, so the intelligent degrade is field-level: an unparseable
/// value reads as null (the override is dropped, the run-level profile applies) rather than the whole decision dying
/// on one bad leaf. Writes stay standard (round-trip safe). Registered ONLY on the decider's deserialize options —
/// canonical payload serialization is untouched.
/// </summary>
public sealed class LenientGuidConverter : JsonConverter<Guid?>
{
    public override Guid? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;

        if (reader.TokenType == JsonTokenType.String && Guid.TryParse(reader.GetString(), out var guid)) return guid;

        reader.Skip();   // a name, a number, an object — a proposal the server could never honor; drop the field, keep the decision

        return null;
    }

    public override void Write(Utf8JsonWriter writer, Guid? value, JsonSerializerOptions options)
    {
        if (value is { } guid) writer.WriteStringValue(guid);
        else writer.WriteNullValue();
    }
}
