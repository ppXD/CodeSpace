using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.ExternalTriggers;

public static class ExternalTriggerJsonEquality
{
    public static bool AreEquivalent(JsonElement left, JsonElement right) =>
        AreEquivalent(left, right, int.MaxValue, out _, out _);

    public static bool AreEquivalent(JsonElement left, JsonElement right, int maxNodes, out int nodesVisited, out bool exhausted)
    {
        var remaining = maxNodes;
        exhausted = false;
        var equivalent = Compare(left, right, ref remaining, ref exhausted);
        nodesVisited = maxNodes - remaining;
        return equivalent;
    }

    private static bool Compare(JsonElement left, JsonElement right, ref int remaining, ref bool exhausted)
    {
        if (remaining == 0) { exhausted = true; return false; }
        remaining--;
        if (left.ValueKind != right.ValueKind) return false;

        if (left.ValueKind == JsonValueKind.Object)
        {
            // Index once so member order does not turn a bounded comparison into repeated object scans.
            var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in right.EnumerateObject())
            {
                if (remaining == 0) { exhausted = true; return false; }
                remaining--;
                if (!properties.TryAdd(property.Name, property.Value)) return false;
            }
            foreach (var property in left.EnumerateObject())
            {
                if (remaining == 0) { exhausted = true; return false; }
                remaining--;
                if (!properties.Remove(property.Name, out var other)
                    || !Compare(property.Value, other, ref remaining, ref exhausted)) return false;
            }
            return properties.Count == 0;
        }

        if (left.ValueKind == JsonValueKind.Array)
        {
            var leftItems = left.EnumerateArray();
            var rightItems = right.EnumerateArray();
            if (left.GetArrayLength() != right.GetArrayLength()) return false;
            using var leftEnumerator = leftItems.GetEnumerator();
            using var rightEnumerator = rightItems.GetEnumerator();
            while (leftEnumerator.MoveNext() && rightEnumerator.MoveNext())
                if (!Compare(leftEnumerator.Current, rightEnumerator.Current, ref remaining, ref exhausted)) return false;
            return true;
        }

        return JsonElement.DeepEquals(left, right);
    }
}
