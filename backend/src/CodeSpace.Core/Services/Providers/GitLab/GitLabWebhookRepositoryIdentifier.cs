using System.Text.Json;
using CodeSpace.Messages.Dtos.Providers;

namespace CodeSpace.Core.Services.Providers.GitLab;

/// <summary>
/// Pulls the project out of a GitLab delivery. Every project-scoped GitLab event — Push, Merge
/// Request, Issue, Note — carries the same <c>project</c> object, so one reader covers all of
/// them and any future one built the same way. Push additionally repeats the id at the top level
/// as <c>project_id</c>, which is the fallback when a payload arrives without the object.
///
/// <para>Sibling of <see cref="GitLabEventNormalizer"/>: same "parse the body, delegate nothing to
/// the caller" shape, answering the question that comes first.</para>
/// </summary>
public sealed class GitLabWebhookRepositoryIdentifier
{
    public WebhookRepositoryIdentity? Identify(string body, IReadOnlyDictionary<string, string> headers)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object) return null;

        var fromProject = ReadProjectObject(root);

        return fromProject ?? ReadTopLevelProjectId(root);
    }

    /// <summary>The <c>project</c> object every project-scoped GitLab hook carries. Null when the payload has none — a group-membership event, a ping.</summary>
    private static WebhookRepositoryIdentity? ReadProjectObject(JsonElement root)
    {
        if (!root.TryGetProperty("project", out var project) || project.ValueKind != JsonValueKind.Object) return null;

        var externalId = ReadNumberAsString(project, "id");
        var fullPath = ReadString(project, "path_with_namespace");

        if (externalId == null && fullPath == null) return null;

        return new WebhookRepositoryIdentity { ExternalId = externalId, FullPath = fullPath };
    }

    /// <summary>Push Hook repeats the id at the top level. Worth reading: it is the one field present even when the <c>project</c> object is trimmed.</summary>
    private static WebhookRepositoryIdentity? ReadTopLevelProjectId(JsonElement root)
    {
        var externalId = ReadNumberAsString(root, "project_id");

        return externalId == null ? null : new WebhookRepositoryIdentity { ExternalId = externalId };
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // GitLab writes project ids as JSON numbers; repository.external_id stores them as text. GetRawText
    // rather than GetInt64 so an id wider than Int64 (or written unexpectedly as a string) still matches
    // instead of throwing on a delivery we could have routed.
    private static string? ReadNumberAsString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => value.GetString(),
            _ => null
        };
    }
}
