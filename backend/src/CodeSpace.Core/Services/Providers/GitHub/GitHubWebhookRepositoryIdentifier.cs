using System.Text.Json;
using CodeSpace.Messages.Dtos.Providers;

namespace CodeSpace.Core.Services.Providers.GitHub;

/// <summary>
/// Pulls the repository out of a GitHub delivery. Every repository-scoped GitHub event — push,
/// pull_request, issues — carries the same <c>repository</c> object, so one reader covers all of
/// them. Organization-only events (membership, team) carry none, and are correctly identified as
/// naming no repository.
///
/// <para>Sibling of <see cref="GitHubEventNormalizer"/>, answering the question that comes first.</para>
/// </summary>
public sealed class GitHubWebhookRepositoryIdentifier
{
    public WebhookRepositoryIdentity? Identify(string body, IReadOnlyDictionary<string, string> headers)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("repository", out var repository) || repository.ValueKind != JsonValueKind.Object) return null;

        var externalId = ReadNumberAsString(repository, "id");
        var fullPath = ReadString(repository, "full_name");

        if (externalId == null && fullPath == null) return null;

        return new WebhookRepositoryIdentity { ExternalId = externalId, FullPath = fullPath };
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // GitHub writes repository ids as JSON numbers; repository.external_id stores them as text.
    // GetRawText for the same reason the GitLab reader uses it — an unexpected width or type should
    // not throw away a delivery we could have routed.
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
