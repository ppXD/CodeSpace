using System.Text.Json;
using CodeSpace.Messages.Dtos.Sessions.Room;

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
    public static RoomDelivery? Parse(string? outputsJson, string? inputsJson) => ParseMany(outputsJson, inputsJson).FirstOrDefault();

    public static IReadOnlyList<RoomDelivery> ParseMany(string? outputsJson, string? inputsJson)
    {
        if (string.IsNullOrWhiteSpace(outputsJson)) return Array.Empty<RoomDelivery>();

        try
        {
            using var document = JsonDocument.Parse(outputsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Array.Empty<RoomDelivery>();

            var inputs = TryParseObject(inputsJson);

            // Multi-repo change set — the pullRequests[] key is PR-specific (an issue node never carries it).
            if (root.TryGetProperty("pullRequests", out var prs) && prs.ValueKind == JsonValueKind.Array)
            {
                var repositories = InputsRepositories(inputs);
                return prs.EnumerateArray().Select((pr, index) => BuildOutcome(pr, inputs, RepositoryInput(pr, repositories, index), index)).Where(d => d != null).Cast<RoomDelivery>().ToList();
            }

            // Single PR — only when the inputs carry a branch, which distinguishes it from an issue that shares the {number,url} shape.
            if (PrFields(root) is { } single && HasBranch(inputs)) return [Build(single, inputs)];

            return Array.Empty<RoomDelivery>();
        }
        catch (JsonException)
        {
            return Array.Empty<RoomDelivery>();
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
        RepositoryAlias = Str(inputs, "alias"),
        Disposition = RoomPullRequestDisposition.Opened,
        Reference = $"#{pr.Number}",
        BranchHead = Str(inputs, "sourceBranch") ?? Str(inputs, "head"),
        BranchBase = Str(inputs, "targetBranch") ?? Str(inputs, "base"),
        Url = pr.Url,
    };

    private static RoomDelivery? BuildOutcome(JsonElement outcome, JsonElement? inputs, JsonElement? repository, int index)
    {
        if (outcome.ValueKind != JsonValueKind.Object) return null;

        var pr = PrFields(outcome);
        var disposition = Enum.TryParse<RoomPullRequestDisposition>(Str(outcome, "disposition"), ignoreCase: true, out var parsed)
            ? parsed
            : pr is not null ? RoomPullRequestDisposition.Opened : (RoomPullRequestDisposition?)null;
        var repositoryId = Guid.TryParse(Str(outcome, "repositoryId") ?? Str(repository, "repositoryId"), out var id) ? id : (Guid?)null;
        var alias = Str(outcome, "alias") ?? Str(repository, "alias") ?? repositoryId?.ToString();

        if (disposition is null && repositoryId is null && alias is null) return null;

        var number = Number(outcome, "number");
        return new RoomDelivery
        {
            Title = Str(inputs, "title") ?? (number is { } n ? $"Pull request #{n}" : alias ?? $"Repository {index + 1}"),
            RepositoryId = repositoryId,
            RepositoryAlias = alias,
            Disposition = disposition,
            Reference = number is { } reference ? $"#{reference}" : null,
            BranchHead = Str(repository, "producedBranch") ?? Str(repository, "sourceBranch") ?? Str(inputs, "sourceBranch") ?? Str(inputs, "head"),
            BranchBase = Str(repository, "baseBranch") ?? Str(repository, "targetBranch") ?? Str(inputs, "targetBranch") ?? Str(inputs, "base"),
            Url = Str(outcome, "url"),
            Error = Str(outcome, "error"),
        };
    }

    private static IReadOnlyList<JsonElement> InputsRepositories(JsonElement? inputs) =>
        inputs is { } value && value.TryGetProperty("repositories", out var repositories) && repositories.ValueKind == JsonValueKind.Array
            ? repositories.EnumerateArray().Select(item => item.Clone()).ToList()
            : Array.Empty<JsonElement>();

    private static JsonElement? RepositoryInput(JsonElement outcome, IReadOnlyList<JsonElement> repositories, int index)
    {
        var id = Str(outcome, "repositoryId");
        if (id is not null)
        {
            var matched = repositories.FirstOrDefault(repository => Str(repository, "repositoryId") == id);
            if (matched.ValueKind != JsonValueKind.Undefined) return matched;
        }
        return index < repositories.Count ? repositories[index] : null;
    }

    private static JsonElement? TryParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { var e = JsonDocument.Parse(json).RootElement; return e.ValueKind == JsonValueKind.Object ? e.Clone() : null; }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonElement? obj, string key) =>
        obj is { } o && o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;

    private static long? Number(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
}
