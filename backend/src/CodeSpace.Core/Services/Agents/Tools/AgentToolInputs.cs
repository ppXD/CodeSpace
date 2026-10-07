using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Nodes;

namespace CodeSpace.Core.Services.Agents.Tools;

/// <summary>
/// A node tool's inputs as the agent-tool path reads them: the keys its schema declares (the only keys a call may
/// send), and the call's target — what a reviewer's rejection sticks to. Pure.
/// </summary>
public static class AgentToolInputs
{
    /// <summary>The input keys <paramref name="schema"/> declares, in its order. None when it declares no properties.</summary>
    public static IReadOnlyList<string> Declared(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
            ? properties.EnumerateObject().Select(property => property.Name).ToList()
            : [];

    /// <summary>
    /// The call's target: <see cref="NodeManifest.ApprovalTargetInputs"/> (else every declared input) as the node reads
    /// them and as the approval card shows them. A repository id is compared as a uuid; text — a string, or one inside a
    /// list or object — has its whitespace put on one line and trimmed, as the card shows it, so two calls whose cards read
    /// the same are the same target; and a value a node reads as absent — null, an empty string, <c>false</c>, an empty
    /// list — is dropped. Dropping <c>false</c> holds while every boolean an agent tool takes defaults to false; were one to
    /// default to true, two different calls would share a target, which refuses a re-ask rather than running anything —
    /// as does text whose spacing matters, the direction this normalising may err in.
    /// </summary>
    public static JsonElement Target(NodeManifest manifest, IReadOnlyDictionary<string, JsonElement> inputs)
    {
        var keys = manifest.ApprovalTargetInputs ?? Declared(manifest.InputSchema);
        var target = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var key in keys)
            if (inputs.TryGetValue(key, out var value) && Normalised(manifest, key, value) is { } normalised)
                target[key] = normalised;

        return JsonSerializer.SerializeToElement(target);
    }

    /// <summary><paramref name="inputs"/> with <paramref name="pins"/> written over them: the inputs an approved call runs with, and so the ones its target is judged by.</summary>
    public static IReadOnlyDictionary<string, JsonElement> WithPins(IReadOnlyDictionary<string, JsonElement> inputs, IReadOnlyDictionary<string, string> pins)
    {
        var pinned = inputs.ToDictionary(pair => pair.Key, pair => pair.Value);

        foreach (var (key, value) in pins) pinned[key] = JsonSerializer.SerializeToElement(value);

        return pinned;
    }

    private static JsonElement? Normalised(NodeManifest manifest, string key, JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False => null,
        JsonValueKind.Array when value.GetArrayLength() == 0 => null,
        JsonValueKind.String => NormalisedText(key == manifest.RepositoryInput?.InputKey, value.GetString() ?? ""),
        _ => AsShown(value),
    };

    private static JsonElement? NormalisedText(bool isRepository, string text)
    {
        var oneLine = ToolCallPreviews.OneLine(text);

        if (oneLine.Length == 0) return null;

        return JsonSerializer.SerializeToElement(isRepository && Guid.TryParse(oneLine, out var id) ? id.ToString("D") : oneLine);
    }

    /// <summary>A list or object with every string in it put on one line and trimmed, as the card shows it; anything else as it is.</summary>
    private static JsonElement AsShown(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => JsonSerializer.SerializeToElement(ToolCallPreviews.OneLine(value.GetString() ?? "")),
        JsonValueKind.Array => JsonSerializer.SerializeToElement(value.EnumerateArray().Select(AsShown).ToList()),
        JsonValueKind.Object => JsonSerializer.SerializeToElement(value.EnumerateObject().ToDictionary(property => property.Name, property => AsShown(property.Value))),
        _ => value,
    };
}
