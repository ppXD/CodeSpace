using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeSpace.Core.Services.Workflows.Planning;

/// <summary>
/// The planner's COMMIT-CONTRACT: the JSON Schema the model is constrained to (via the structured-output
/// path) and the matching deserialization options. Co-located with the planner concern (Rule 18) and
/// pinned by a unit test — a drift in either the schema or the property mapping is a contract change a
/// reviewer must see, not an invisible refactor.
///
/// <para>The schema's property names map 1:1 onto <c>PlannedWorkflow</c> / <c>PlannedSubtask</c> (the
/// options are case-insensitive so the model's <c>goal</c> binds to <c>Goal</c>). <c>additionalProperties:
/// false</c> + <c>required</c> keep the model from inventing fields or dropping the ones the projector
/// depends on; <c>subtasks</c> is bounded <c>[1,20]</c> so a plan always fans out at least one branch and
/// never an unbounded wave.</para>
/// </summary>
public static class PlannerSchema
{
    /// <summary>The JSON-Schema (root object) the structured-LLM call is constrained to. The deserialized <c>PlannedWorkflow</c> round-trips from any object that conforms.</summary>
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
                  "rationale": { "type": "string", "description": "Optional one-line 'why this subtask'." }
                },
                "required": ["id", "title", "instruction"]
              }
            },
            "successCriteria": { "type": "array", "items": { "type": "string" }, "description": "Observable conditions that, together, mean the goal is done." },
            "risks": { "type": "array", "items": { "type": "string" }, "description": "Risks / unknowns the plan carries." },
            "recommendedWorkflowKind": { "type": "string", "description": "Execution shape per branch: 'coding' projects onto an agent; anything else onto an LLM step." }
          },
          "required": ["goal", "subtasks"]
        }
        """).RootElement.Clone();

    /// <summary>Deserialization options for mapping a schema-valid object back into <c>PlannedWorkflow</c>. Case-insensitive so the model's lower-camel keys bind to the record's Pascal properties.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
