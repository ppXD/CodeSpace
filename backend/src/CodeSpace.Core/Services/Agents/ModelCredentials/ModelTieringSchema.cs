using System.Text.Json;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Agents.ModelCredentials;

/// <summary>
/// The structured-output contract for the capability-tiering call (mirrors <c>LlmEffortClassifierSchema</c>): the brain
/// is handed a batch of pool model ids and returns ONE tier per id, inferred from the id text alone. An id the brain
/// cannot recognise (an opaque / renamed gateway alias) is tiered <c>unknown</c> — the cached hook a later objective
/// probe slice fills. Co-located with the producer (Rule 18) and pinned by a unit test.
/// </summary>
public static class ModelTieringSchema
{
    /// <summary>The JSON-Schema the structured call is constrained to: a list of {id, tier} where tier ∈ frontier/strong/basic/unknown.</summary>
    public static readonly JsonElement ResponseSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "models": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "id": { "type": "string", "description": "The model id, exactly as given." },
                  "tier": { "type": "string", "enum": ["frontier", "strong", "basic", "unknown"], "description": "Coding capability inferred from the id alone; 'unknown' if you do not recognise the id." }
                },
                "required": ["id", "tier"]
              }
            }
          },
          "required": ["models"]
        }
        """).RootElement.Clone();

    /// <summary>Case-insensitive so the model's lower-camel keys bind. Tier is read as a raw string and mapped via <see cref="ParseTier"/> (no enum converter — an unrecognised value floors to Unknown, never throws).</summary>
    public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Map a model-authored tier string to <see cref="ModelCapabilityTier"/>, case-insensitively; anything unrecognised / blank → <see cref="ModelCapabilityTier.Unknown"/> (fail-soft — a malformed tier is just "not tiered").</summary>
    public static ModelCapabilityTier ParseTier(string? tier) => tier?.Trim().ToLowerInvariant() switch
    {
        "frontier" => ModelCapabilityTier.Frontier,
        "strong" => ModelCapabilityTier.Strong,
        "basic" => ModelCapabilityTier.Basic,
        _ => ModelCapabilityTier.Unknown,
    };
}

/// <summary>The tiering reply (Rule 18.1 — a data noun): one tier per pool model id.</summary>
public sealed record ModelTierAssignments(IReadOnlyList<ModelTierAssignment> Models);

/// <summary>One id→tier assignment from the brain. <c>Tier</c> is the raw string mapped via <see cref="ModelTieringSchema.ParseTier"/>.</summary>
public sealed record ModelTierAssignment(string? Id, string? Tier);
