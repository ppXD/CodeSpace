using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeSpace.Core.Services.Supervisor.Deciders;

/// <summary>
/// Deterministic repair of the ONE malformation the model reliably authors: writing the chosen kind's payload fields at
/// the ROOT of the decision instead of inside that kind's sub-object — <c>{"kind":"retry","subtaskId":"s2",…}</c> rather
/// than <c>{"kind":"retry","retry":{"subtaskId":"s2",…}}</c>. The gateway 200s on the forced-tool schema either way (a
/// per-kind conditional <c>required</c> needs <c>if/then</c>, which the wire accepts but does not enforce), so the shape
/// arrives valid-but-unreadable and <see cref="SupervisorDecisionCoherence"/> names it.
///
/// <para>WHY DETERMINISTIC: the flattened reply already contains every field the payload needs, in the right names, with
/// the right values — only the nesting is wrong. Asking the model to fix that is a second round-trip to recover
/// information the first one already delivered, and it only works while the repair model happens to comply. Live count
/// from one supervisor eval run on 2026-08-19: 68 flattened decisions (46 spawn, 21 retry, 1 stop) — 68 avoidable
/// round-trips. This lift runs first; the model repair stays as the fallback for a reply this cannot fix (a genuinely
/// absent payload, where no amount of moving fields invents one).</para>
///
/// <para>WHY IT IS GENERIC rather than a per-kind rescue: both the kind→sub-object mapping and each sub-object's field
/// names are read from <see cref="SupervisorDecisionSchema.ResponseSchema"/> at first use, never from a table here. A
/// kind added to the schema is lifted with no change to this file, and a field renamed in the schema follows
/// automatically. The one convention this relies on — a kind's sub-object is its <c>snake_case</c> verb in camelCase
/// (<c>ask_human</c> → <c>askHuman</c>) — holds for every kind in the vocabulary today and is pinned by a drift test
/// that fails when a kind ships a payload the derivation cannot find.</para>
///
/// <para>CONSERVATIVE BY CONSTRUCTION: it only ever MOVES a root property whose name is a declared field of the target
/// sub-object, never renames, never invents, never overwrites a value already inside the sub-object, and returns null
/// when there is nothing to move. So a reply this misreads cannot become a decision the model did not author — the worst
/// case is that it declines and the model repair runs exactly as it did before.</para>
/// </summary>
internal static class SupervisorDecisionPayloadLift
{
    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlySet<string>>> PayloadFields = new(BuildPayloadFields);
    private static readonly Lazy<IReadOnlySet<string>> RootProperties = new(BuildRootProperties);

    /// <summary>The sub-object name for a decision kind, or null when the schema declares no payload for it (<c>resolve</c>).</summary>
    public static string? PayloadPropertyFor(string kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return null;

        var flattened = kind.Replace("_", "", StringComparison.Ordinal);

        return PayloadFields.Value.Keys.FirstOrDefault(name => string.Equals(name, flattened, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The field names the schema declares inside a kind's sub-object. Empty when the kind has no sub-object.</summary>
    public static IReadOnlySet<string> FieldsOf(string kind) =>
        PayloadPropertyFor(kind) is { } name ? PayloadFields.Value[name] : new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// The decision with its flattened payload fields moved into the sub-object its kind names, or null when nothing
    /// could be moved — a non-object reply, a kind with no sub-object, or a root that carries none of the payload's fields.
    /// </summary>
    public static JsonElement? Lift(JsonElement decision, string kind)
    {
        if (decision.ValueKind != JsonValueKind.Object) return null;
        if (PayloadPropertyFor(kind) is not { } payloadName) return null;

        var fields = PayloadFields.Value[payloadName];
        var root = JsonNode.Parse(decision.GetRawText())!.AsObject();
        var movable = root.Select(pair => pair.Key).Where(key => fields.Contains(key) && !RootProperties.Value.Contains(key)).ToList();

        if (movable.Count == 0) return null;

        var payload = root[payloadName] as JsonObject ?? new JsonObject();

        foreach (var key in movable)
        {
            var value = root[key];
            root.Remove(key);

            // Never overwrite what the model DID nest — a half-flattened reply keeps its own authored value.
            if (payload.ContainsKey(key)) continue;

            payload[key] = value?.DeepClone();
        }

        root[payloadName] = payload;

        return JsonDocument.Parse(root.ToJsonString()).RootElement.Clone();
    }

    /// <summary>Every top-level object property the schema declares whose own <c>properties</c> map is non-empty — the payload sub-objects, keyed by name.</summary>
    private static IReadOnlyDictionary<string, IReadOnlySet<string>> BuildPayloadFields()
    {
        var map = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

        foreach (var property in SupervisorDecisionSchema.ResponseSchema.GetProperty("properties").EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object) continue;
            if (!property.Value.TryGetProperty("properties", out var fields) || fields.ValueKind != JsonValueKind.Object) continue;

            var names = fields.EnumerateObject().Select(field => field.Name).ToHashSet(StringComparer.Ordinal);

            if (names.Count > 0) map[property.Name] = names;
        }

        return map;
    }

    /// <summary>The names that legitimately live at the decision's root, so a payload field sharing one is never moved out from under it.</summary>
    private static IReadOnlySet<string> BuildRootProperties() =>
        SupervisorDecisionSchema.ResponseSchema.GetProperty("properties").EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
}
