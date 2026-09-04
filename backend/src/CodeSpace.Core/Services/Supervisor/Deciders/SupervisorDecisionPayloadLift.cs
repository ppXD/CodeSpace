using System.Text.Json;
using System.Text.Json.Nodes;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor.Deciders;

/// <summary>
/// Deterministic repair of the two malformations the model reliably authors. <see cref="Lift"/> owns the first and
/// dominant one; <see cref="LiftStopNarration"/> owns the terminal-stop case the first cannot reach (see its own doc).
///
/// <para><see cref="Lift"/>: writing the chosen kind's payload fields at
/// the ROOT of the decision instead of inside that kind's sub-object — <c>{"kind":"retry","subtaskId":"s2",…}</c> rather
/// than <c>{"kind":"retry","retry":{"subtaskId":"s2",…}}</c>. The gateway 200s on the forced-tool schema either way (a
/// per-kind conditional <c>required</c> needs <c>if/then</c>, which the wire accepts but does not enforce), so the shape
/// arrives valid-but-unreadable and <see cref="SupervisorDecisionCoherence"/> names it.</para>
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

    /// <summary>
    /// The NARRATION floor for <c>stop</c>: the decision with its <c>stop.summary</c> recovered from the root
    /// <c>rationale</c> the model DID author, or null when there is nothing to recover (a summary already present,
    /// no rationale prose, any other kind, a non-object reply).
    ///
    /// <para>WHY A SECOND REPAIR EXISTS: <see cref="Lift"/> moves fields; the live shape it cannot fix is a decision
    /// carrying ONLY <c>kind</c> + <c>rationale</c> — no <c>stop</c> object and no stop field at the root (real-model
    /// run 33755336097, 2026-09-03: three attempts, every one refused). Left unrepaired, the projector substitutes an
    /// EMPTY summary and <see cref="SupervisorPublishGate"/> then rejects the terminal stop of a run that HAS published
    /// work — "the run has published work but no summary" — and substitutes an <c>ask_human</c>, parking a finished run
    /// on a question no human owes. The words that answer it are already in the reply.</para>
    ///
    /// <para>WHY <c>stop</c> ALONE, and why this is not the generic lift widened: every other verb's payload names
    /// ENTITIES the run must then act on — a subtask id to re-run, a question to ask, a replacement oracle. Prose cannot
    /// yield those, and a guess would fan out work the model never chose. <c>stop</c> commands nothing: its payload only
    /// DESCRIBES the ending, which is exactly what the rationale describes. So this recovers WORDS THE MODEL WROTE and
    /// never authors any of its own.</para>
    ///
    /// <para>IT NEVER STRENGTHENS THE TERMINAL CLAIM, and this is the whole reason the fill is FAIL-CLOSED. An
    /// <c>outcome</c> the model nested is kept verbatim. An ABSENT one is filled with <see cref="AssumedGiveUpOutcome"/>
    /// — never a success label — because the recovered prose is the model's REASONING, not a terminal verdict: a
    /// rationale reading "I could not finish" would otherwise terminalize as a SUCCESSFUL stop carrying that very text.
    /// The empty summary used to be the accidental backstop (the publish gate parked such a run); replacing it with a
    /// summary means the honesty must now be carried explicitly. The assumption is recorded in the payload's
    /// <c>outcomeAssumed</c> so the journal says the label was the server's, not the model's — and it is a fixed
    /// constant, never inferred from the prose: matching English give-up words would be a classifier no one could
    /// audit. A unit test pins the constant to a NON-success, NON-clarification classification rather than to its
    /// spelling.</para>
    ///
    /// <para>This deliberately DIFFERS from <see cref="SupervisorDecisionProjector"/>'s own substitute, which covers a
    /// different path: a stop that reached projection with NO payload and NO recovered words, where nothing was claimed
    /// on the model's behalf because nothing was said at all. This path claims words, so it must not also claim a win.</para>
    /// </summary>
    public static JsonElement? LiftStopNarration(JsonElement decision)
    {
        if (decision.ValueKind != JsonValueKind.Object) return null;
        if (!IsKind(decision, SupervisorDecisionKinds.Stop)) return null;

        var root = JsonNode.Parse(decision.GetRawText())!.AsObject();
        var stop = root[StopProperty] as JsonObject;

        if (stop is not null && StringField(stop, SummaryField) is not null) return null;

        if (NarrationFrom(root) is not { } narration) return null;

        stop = stop is null ? new JsonObject() : (JsonObject)stop.DeepClone();
        stop[SummaryField] = narration;

        if (StringField(stop, OutcomeField) is null)
        {
            stop[OutcomeField] = AssumedGiveUpOutcome;
            stop[OutcomeAssumedField] = AssumedOutcomeNote;
        }

        root[StopProperty] = stop;

        return JsonDocument.Parse(root.ToJsonString()).RootElement.Clone();
    }

    /// <summary>The NON-success terminal label a narrated stop carries when the model authored none. Fixed, never inferred from the prose. Pinned by a drift test against <c>SupervisorStopPayload</c>'s own classification (Rule 8) — the pin is on the reading, not the spelling.</summary>
    public const string AssumedGiveUpOutcome = "gave_up";

    /// <summary>What the payload records alongside an assumed outcome, so a reader can tell a server assumption from a model verdict.</summary>
    public const string AssumedOutcomeNote = "gave_up (no outcome authored)";

    /// <summary>The model's own prose off the root <c>rationale</c> — its <c>why</c>, then the <c>evidence</c> it cited — or null when it authored neither.</summary>
    private static string? NarrationFrom(JsonObject root)
    {
        if (root[RationaleProperty] is not JsonObject rationale) return null;

        var parts = new[] { StringField(rationale, "why"), StringField(rationale, "evidence") }.Where(part => part is not null).ToList();

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    /// <summary>A non-blank string property, trimmed — null for absent, blank, or a non-string value the model wrote where prose belongs.</summary>
    private static string? StringField(JsonObject owner, string name) =>
        owner[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    private static bool IsKind(JsonElement decision, string kind) =>
        decision.TryGetProperty("kind", out var value) && value.ValueKind == JsonValueKind.String && value.GetString() == kind;

    private const string StopProperty = "stop";
    private const string RationaleProperty = "rationale";
    private const string SummaryField = "summary";
    private const string OutcomeField = "outcome";
    private const string OutcomeAssumedField = "outcomeAssumed";

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
