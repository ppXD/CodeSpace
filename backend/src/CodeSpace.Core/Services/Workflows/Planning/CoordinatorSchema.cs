using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeSpace.Core.Services.Workflows.Planning;

/// <summary>
/// The coordinator's COMMIT-CONTRACT for an L3 checkpoint-coordinated plan: the JSON Schema the coordinator
/// <c>llm.complete</c> node is constrained to (structured output), surfaced on its <c>json</c> output. The
/// enclosing <c>flow.loop</c> reads <c>decision</c> (termination) and <c>reworkSubtasks</c> (next round's
/// fan-out) off it. Co-located with the planner concern (Rule 18) and pinned by a unit test — a drift in the
/// schema or the property mapping is a contract change a reviewer must see, mirroring <c>PlannerSchema</c>.
///
/// <para>The schema's property names map 1:1 onto <c>CoordinatorDecision</c> / <c>PlannedSubtask</c> (the
/// options are case-insensitive). <c>additionalProperties: false</c> + <c>required [decision]</c> keep the
/// model from inventing fields or dropping the one the loop's termination depends on. <c>reworkSubtasks</c>
/// reuses the planner's exact subtask shape so a rework round re-seeds the same <c>{{item.title}}</c> /
/// <c>{{item.instruction}}</c> body refs the map already binds.</para>
/// </summary>
public static class CoordinatorSchema
{
    /// <summary>The JSON-Schema (root object) the coordinator's structured-LLM call is constrained to. A schema-valid object round-trips into <c>CoordinatorDecision</c>.</summary>
    public static readonly JsonElement ResponseSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "decision": { "type": "string", "enum": ["done", "rework", "ask_human", "abort"], "description": "Round verdict: 'done'/'abort' stop the rounds; 'rework' runs another round over reworkSubtasks; 'ask_human' requests a human (reserved)." },
            "summary": { "type": "string", "description": "One-paragraph summary of the round's results and the reasoning behind the decision." },
            "reworkSubtasks": {
              "type": "array",
              "maxItems": 20,
              "description": "The next round's subtasks when decision == 'rework'. Same shape as the planner's subtasks.",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "id": { "type": "string", "description": "Stable, plan-local id for the subtask." },
                  "title": { "type": "string", "description": "Short human title." },
                  "instruction": { "type": "string", "description": "The concrete instruction the branch executes." },
                  "rationale": { "type": "string", "description": "Optional one-line 'why this subtask'." }
                },
                "required": ["id", "title", "instruction"]
              }
            },
            "question": { "type": "string", "description": "The question to a human when decision == 'ask_human'." },
            "riskLevel": { "type": "string", "enum": ["low", "medium", "high"], "description": "The coordinator's risk read of its own decision." }
          },
          "required": ["decision"]
        }
        """).RootElement.Clone();

    /// <summary>Deserialization options for mapping a schema-valid object back into <c>CoordinatorDecision</c>. Case-insensitive so the model's lower-camel keys bind to the record's Pascal properties.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
