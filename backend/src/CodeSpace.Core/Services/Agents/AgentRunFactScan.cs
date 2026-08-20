using System.Text.Json;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// The scan the three run-fact readers share once a harness's <c>AgentRunFactKeys</c> says WHERE to look: walk the
/// declared containers of one structured payload, and read a key out of one of them. Pure + stateless, and total —
/// a path that does not resolve to an object is skipped, never an error, because a stream carries different shapes
/// line by line and a missing nesting is the normal case rather than a fault.
/// </summary>
internal static class AgentRunFactScan
{
    /// <summary>The payload root FIRST, then each declared dotted path that resolves to an object — the bounded, ordered set of places one fact may live in this payload.</summary>
    internal static IEnumerable<JsonElement> Containers(JsonElement data, IReadOnlyList<string> paths)
    {
        if (data.ValueKind != JsonValueKind.Object) yield break;

        yield return data;

        foreach (var path in paths)
            if (TryResolve(data, path, out var child)) yield return child;
    }

    /// <summary>The first key's non-empty string value, or null — the shape both the session id and the model are read with.</summary>
    internal static string? ReadString(JsonElement obj, IReadOnlyList<string> keys)
    {
        foreach (var key in keys)
            if (obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s) return s;

        return null;
    }

    /// <summary>The first key's 32-bit integer value.</summary>
    internal static bool TryReadInt(JsonElement obj, IReadOnlyList<string> keys, out int value)
    {
        foreach (var key in keys)
            if (obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out value)) return true;

        value = 0;
        return false;
    }

    /// <summary>Walk a dotted path from the payload root; every segment must resolve to an object for the container to exist.</summary>
    private static bool TryResolve(JsonElement data, string path, out JsonElement child)
    {
        child = data;

        foreach (var range in path.AsSpan().Split('.'))
        {
            if (!child.TryGetProperty(path.AsSpan()[range], out var next) || next.ValueKind != JsonValueKind.Object) return false;

            child = next;
        }

        return true;
    }
}
