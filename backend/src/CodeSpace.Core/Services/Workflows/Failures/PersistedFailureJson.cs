using System.Text.Json;
using System.Text.Json.Serialization;
using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Workflows.Failures;

/// <summary>
/// How a <see cref="PersistedFailure"/> sits in a ledger record's payload: an additive <c>failure</c> object beside the
/// <c>error</c> sentence it explains. A record written before failures were kept simply has no such key, and reads as
/// "no failure" — the sentence keeps rendering as it always did.
/// </summary>
public static class PersistedFailureJson
{
    /// <summary>The payload key the object is written under.</summary>
    public const string PayloadKey = "failure";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>The object to put under <see cref="PayloadKey"/>.</summary>
    public static JsonElement ToElement(PersistedFailure failure) => JsonSerializer.SerializeToElement(failure, Options);

    /// <summary>A payload holding only the failure — <c>{"failure": {...}}</c> — for a row whose JSON column has no sentence beside it, read back by <see cref="Read"/>.</summary>
    public static string AsPayload(PersistedFailure failure) => JsonSerializer.Serialize(new Dictionary<string, JsonElement> { [PayloadKey] = ToElement(failure) });

    /// <summary>The failure a record's payload carries, or null when it carries none or what it carries cannot be read — never throws, because a record that cannot be read must degrade to the sentence, not fail the page that shows it.</summary>
    public static PersistedFailure? Read(string? payloadJson)
    {
        if (string.IsNullOrEmpty(payloadJson)) return null;

        try
        {
            using var document = JsonDocument.Parse(payloadJson);

            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(PayloadKey, out var failure) ? ReadElement(failure) : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The failure an element holds (an agent result's <c>failure</c>, or a record's), or null when it is not one.</summary>
    public static PersistedFailure? ReadElement(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        try { return element.Deserialize<PersistedFailure>(Options) is { Code.Length: > 0 } failure ? failure : null; }
        catch (JsonException) { return null; }
    }
}
