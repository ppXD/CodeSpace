using System.Text.Json;

namespace CodeSpace.Core.Services.Supervisor.Deciders;

/// <summary>
/// The supervisor decider's COMMIT-CONTRACT (PR-E E3): the JSON Schema the model is constrained to via the
/// structured-output path, and the matching deserialization options. Co-located with the decider concern
/// (Rule 18) and pinned by a unit test — a drift in the schema is a contract change a reviewer must see, not
/// an invisible refactor. Mirrors <c>PlannerSchema.ResponseSchema</c>.
///
/// <para>The model picks ONE <c>kind</c> from the six-verb vocabulary and supplies that verb's bounded
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
              "enum": ["plan", "spawn", "retry", "ask_human", "merge", "stop"],
              "description": "The single next action. 'plan' decomposes the goal; 'spawn' fans out agents over planned subtasks; 'retry' re-runs one subtask; 'merge' synthesizes prior agent results; 'ask_human' asks a question; 'stop' ends the run."
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
                      "instruction": { "type": "string", "description": "The concrete instruction a spawned agent executes." }
                    },
                    "required": ["id", "title", "instruction"]
                  }
                }
              },
              "required": ["goal", "subtasks"],
              "description": "Required when kind == 'plan'."
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
                }
              },
              "required": ["subtaskIds"],
              "description": "Required when kind == 'spawn'."
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
                "summary": { "type": "string", "description": "Short summary of what the supervisor accomplished." }
              },
              "required": ["outcome", "summary"],
              "description": "Required when kind == 'stop'."
            }
          },
          "required": ["kind"]
        }
        """).RootElement.Clone();

    /// <summary>Deserialization options for mapping a schema-valid object back into <c>SupervisorModelDecision</c>. Case-insensitive so the model's lower-camel keys bind to the record's Pascal properties.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}
