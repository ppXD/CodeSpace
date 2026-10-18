using System.Text.Json;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;

namespace CodeSpace.Core.Services.Providers.GitLab;

/// <summary>
/// A merge request author's standing on the project, for the trigger's authors filter. GitLab's hook names the author
/// and not their access level, so this asks <c>GET /projects/:id/members/all/:user_id</c> — "all" so a role inherited
/// from a parent group counts like a direct one.
///
/// <para>Only two answers say anything about the author: 200 (their role) and 404 (they hold none). Every other answer is
/// not an answer about the author — a 401 or 403 for the credential, a 429 or a 5xx for GitLab — so it is thrown as
/// <see cref="ProviderApiException"/>, carrying the status, rather than returned as <c>Unknown</c>, which a members-only
/// trigger reads as "not a member" and refuses the author's own pull request for. A 429 or 5xx is asked again once
/// first (<see cref="StandingRetryDelay"/>), inside the caller's deadline.</para>
/// </summary>
public sealed partial class GitLabRepositoryProvider : IRepositoryMemberStandingCapability
{
    /// <summary>Developer: the first role that can push to the project — GitLab's counterpart of a GitHub collaborator.</summary>
    internal const int MemberAccessLevelFloor = 30;

    /// <summary>The wait before a 429 or 5xx is asked again. Short on purpose: the whole lookup has the trigger's few seconds (<c>PullRequestTriggerAdmission.AuthorLookupTimeout</c>), and a longer <c>Retry-After</c> is not waited out.</summary>
    internal static readonly TimeSpan StandingRetryDelay = TimeSpan.FromMilliseconds(250);

    public async Task<PullRequestAuthorAssociation> GetMemberStandingAsync(ProviderContext context, RemoteRepository repository, string userExternalId, CancellationToken cancellationToken)
    {
        var (host, credential) = await ResolveCredentialAsync(context, cancellationToken).ConfigureAwait(false);
        var url = $"{host.TrimEnd('/')}/api/v4/projects/{Uri.EscapeDataString(repository.ExternalId)}/members/all/{Uri.EscapeDataString(userExternalId)}";

        using var response = await SendStandingRequestAsync(url, credential, cancellationToken).ConfigureAwait(false);

        if ((int)response.StatusCode == 404) return PullRequestAuthorAssociation.None;
        if (!response.IsSuccessStatusCode) throw StandingRefused(response);

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return MapMemberStanding(ParseMemberAccessLevel(json));
    }

    /// <summary>The lookup, asked again once after <see cref="StandingRetryDelay"/> if GitLab answered 429 or 5xx.</summary>
    private static async Task<HttpResponseMessage> SendStandingRequestAsync(string url, GitLabCredential credential, CancellationToken cancellationToken)
    {
        using var first = new HttpRequestMessage(HttpMethod.Get, url);

        var response = await GitLabHttp.SendAsync(first, credential, cancellationToken).ConfigureAwait(false);

        if (!IsWorthAskingAgain(response.StatusCode)) return response;

        response.Dispose();

        await Task.Delay(StandingRetryDelay, cancellationToken).ConfigureAwait(false);

        using var second = new HttpRequestMessage(HttpMethod.Get, url);

        return await GitLabHttp.SendAsync(second, credential, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsWorthAskingAgain(System.Net.HttpStatusCode status) => (int)status == 429 || (int)status >= 500;

    /// <summary>
    /// The answer that is not an answer about the author, as the failure it is: GitLab's status and reason phrase, never its
    /// body (an error page can echo what it was sent). Raised here, where the status is known, rather than by the resilience
    /// layer: this call does not pass through it, and its retry is the one above.
    /// </summary>
    private static ProviderApiException StandingRefused(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        var reason = $"{status} {response.ReasonPhrase}".TrimEnd();

        return new ProviderApiException(ProviderKind.GitLab, status, nameof(GetMemberStandingAsync), reason, new HttpRequestException(reason, null, response.StatusCode));
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
