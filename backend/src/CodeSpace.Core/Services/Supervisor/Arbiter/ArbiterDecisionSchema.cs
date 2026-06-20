using System.Text.Json;

namespace CodeSpace.Core.Services.Supervisor.Arbiter;

/// <summary>
/// The decision-arbiter's COMMIT-CONTRACT (Decision substrate D4c) — the JSON Schema the brain is constrained to when
/// deciding whether to ANSWER a pending child decision itself or ESCALATE it to a human, plus the matching
/// deserialization options. Co-located with the arbiter (Rule 18) and pinned by a unit test (a schema drift is a visible
/// contract change). Mirrors <c>SupervisorDecisionSchema</c>: <c>additionalProperties:false</c>, a small kind enum, a
/// per-kind payload, and NO graph / id reference — the model only judges the decision; the SERVER routes the verdict
/// (answer via the floor-checked supervisor-author path, or escalate via the existing HITL park).
/// </summary>
public static class ArbiterDecisionSchema
{
    public static readonly JsonElement ResponseSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "kind": {
              "type": "string",
              "enum": ["answer", "escalate"],
              "description": "'answer' to resolve the decision yourself — ONLY for a low/medium-risk decision you are confident about, choosing the raiser's recommended option unless you have a clear reason not to. 'escalate' to send it to a human — when you are unsure, the stakes are high, or the recommendation/context is insufficient. When in doubt, escalate."
            },
            "answer": {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "selectedOptions": { "type": "array", "items": { "type": "string" }, "description": "The chosen option id(s) — one for confirm/choose_one, more for choose_many. Each MUST be one of the decision's option ids." },
                "freeText": { "type": "string", "description": "A free-text answer — for a free_text decision (no options)." }
              },
              "required": [],
              "description": "Required when kind == 'answer' — the answer to record. Omit when escalating."
            },
            "rationale": {
              "type": "string",
              "description": "Why you answered or escalated — REQUIRED. An auto-answer is never silent (it records the rationale); an escalation tells the human why the decision came to them."
            }
          },
          "required": ["kind", "rationale"]
        }
        """).RootElement.Clone();

    /// <summary>Deserialization options for mapping a schema-valid object into the arbiter's model record — case-insensitive so the model's lower-camel keys bind.</summary>
    public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
}
