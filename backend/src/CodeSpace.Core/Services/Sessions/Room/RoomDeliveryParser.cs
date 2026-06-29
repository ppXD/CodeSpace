using System.Text.Json;

namespace CodeSpace.Core.Services.Sessions.Room;

/// <summary>
/// Pure detection of the PR / change set a turn delivered, from a workflow node's outputs + inputs. A PR-open node's
/// output shares its <c>{ number, url, state }</c> shape with an ISSUE node, so shape alone is ambiguous — a single PR
/// is recognized ONLY when the node's INPUTS carry a branch (a PR has source/target branches; an issue never does),
/// and the multi-repo case is recognized by the PR-specific <c>pullRequests[]</c> key. The number is read as int64 so
/// a large PR id never throws. No I/O — unit-tested.
/// </summary>
public static class RoomDeliveryParser
{
    public static RoomDelivery? Parse(string? outputsJson, string? inputsJson)
    {
        if (string.IsNullOrWhiteSpace(outputsJson)) return null;

        try
        {
            var root = JsonDocument.Parse(outputsJson).RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var inputs = TryParseObject(inputsJson);

            // Multi-repo change set — the pullRequests[] key is PR-specific (an issue node never carries it).
            if (root.TryGetProperty("pullRequests", out var prs) && prs.ValueKind == JsonValueKind.Array)
                foreach (var pr in prs.EnumerateArray())
                    if (PrFields(pr) is { } multi) return Build(multi, inputs);

            // Single PR — only when the inputs carry a branch, which distinguishes it from an issue that shares the {number,url} shape.
            if (PrFields(root) is { } single && HasBranch(inputs)) return Build(single, inputs);

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool HasBranch(JsonElement? inputs) =>
        Str(inputs, "sourceBranch") != null || Str(inputs, "targetBranch") != null || Str(inputs, "head") != null;

    private static (long Number, string Url)? PrFields(JsonElement el) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty("number", out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt64(out var number)
        && el.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(u.GetString())
            ? (number, u.GetString()!)
            : null;

    private static RoomDelivery Build((long Number, string Url) pr, JsonElement? inputs) => new()
    {
        Title = Str(inputs, "title") ?? $"Pull request #{pr.Number}",
        Reference = $"#{pr.Number}",
        BranchHead = Str(inputs, "sourceBranch") ?? Str(inputs, "head"),
        BranchBase = Str(inputs, "targetBranch") ?? Str(inputs, "base"),
        Url = pr.Url,
    };

    private static JsonElement? TryParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { var e = JsonDocument.Parse(json).RootElement; return e.ValueKind == JsonValueKind.Object ? e.Clone() : null; }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonElement? obj, string key) =>
        obj is { } o && o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;
}
