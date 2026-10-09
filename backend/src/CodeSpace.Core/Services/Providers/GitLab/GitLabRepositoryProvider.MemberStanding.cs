using System.Text.Json;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Providers.GitLab;

/// <summary>
/// A merge request author's standing on the project, for the trigger's authors filter. GitLab's hook names the author
/// and not their access level, so this asks <c>GET /projects/:id/members/all/:user_id</c> — "all" so a role inherited
/// from a parent group counts like a direct one.
/// </summary>
public sealed partial class GitLabRepositoryProvider : IRepositoryMemberStandingCapability
{
    /// <summary>Developer: the first role that can push to the project — GitLab's counterpart of a GitHub collaborator.</summary>
    internal const int MemberAccessLevelFloor = 30;

    public async Task<PullRequestAuthorAssociation> GetMemberStandingAsync(ProviderContext context, RemoteRepository repository, string userExternalId, CancellationToken cancellationToken)
    {
        var auth = await _authResolver.ResolveAsync(context, cancellationToken).ConfigureAwait(false);
        var host = string.IsNullOrWhiteSpace(context.Instance.ApiUrl) ? context.Instance.BaseUrl : context.Instance.ApiUrl;
        var url = $"{host.TrimEnd('/')}/api/v4/projects/{Uri.EscapeDataString(repository.ExternalId)}/members/all/{Uri.EscapeDataString(userExternalId)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {auth.Token}");
        request.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", auth.Token);

        using var response = await _countsHttpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);

        if ((int)response.StatusCode == 404) return PullRequestAuthorAssociation.None;
        if (!response.IsSuccessStatusCode) return PullRequestAuthorAssociation.Unknown;

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return MapMemberStanding(ParseMemberAccessLevel(json));
    }

    /// <summary>Developer and above is a member; Guest and Reporter can see the project but hold no say over its code.</summary>
    internal static PullRequestAuthorAssociation MapMemberStanding(int? accessLevel) =>
        accessLevel >= MemberAccessLevelFloor ? PullRequestAuthorAssociation.Member : PullRequestAuthorAssociation.None;

    /// <summary>The member body's <c>access_level</c>, or null when it is missing, not a number, or the body is not JSON.</summary>
    internal static int? ParseMemberAccessLevel(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("access_level", out var level) && level.ValueKind == JsonValueKind.Number ? level.GetInt32() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
