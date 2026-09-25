using System.Net;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Providers.Diagnostics;
using CodeSpace.Core.Services.Providers.Resilience;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;

namespace CodeSpace.Core.Services.Providers.GitLab;

/// <summary>
/// Group hooks — the same three operations as the project ones, against
/// <c>/api/v4/groups/:id/hooks</c>. Raw HTTP rather than NGitLab because NGitLab wraps project
/// hooks and not group hooks, and the endpoint is the whole point: a hook that lands on the project
/// route registers perfectly and covers one repository instead of the group.
///
/// <para>GitLab addresses a nested group by its URL-ENCODED full path — <c>acme%2Fplatform</c>. A
/// raw slash resolves to a different route entirely, which answers 404 and reads like "the group is
/// not there".</para>
/// </summary>
public sealed partial class GitLabRepositoryProvider : IConnectionWebhookRegistrationCapability, IWebhookRepositoryIdentifier
{
    /// <summary>What GitLab's own docs call the tier that includes group webhooks.</summary>
    private const string GroupHookPlan = "Premium";

    private const string GroupHookFeature = "group webhooks";

    /// <summary>GitLab's largest page. Its default is 20, and a lookup that reads one page cannot see a hook past it.</summary>
    private const int GroupHooksPageSize = 100;

    public WebhookRepositoryIdentity? Identify(string body, IReadOnlyDictionary<string, string> headers) => _repositoryIdentifier.Identify(body, headers);

    public async Task<RemoteWebhook?> FindConnectionWebhookByCallbackUrlAsync(ProviderContext context, string ownerPath, string callbackUrl, CancellationToken cancellationToken)
    {
        var (_, host, token) = await BuildAuthedAsync(context, cancellationToken).ConfigureAwait(false);
        var url = BuildGroupHooksUrl(host, ownerPath, null);

        var hooks = await _resilience.ExecuteAsync(context.Instance, nameof(FindConnectionWebhookByCallbackUrlAsync), _ => ListGroupHooksAsync(url, token, cancellationToken), cancellationToken).ConfigureAwait(false);

        return MatchGroupHookByCallbackUrl(hooks, callbackUrl);
    }

    public async Task<RemoteWebhook> RegisterConnectionWebhookAsync(ProviderContext context, string ownerPath, WebhookRegistration request, CancellationToken cancellationToken)
    {
        var (_, host, token) = await BuildAuthedAsync(context, cancellationToken).ConfigureAwait(false);
        var url = BuildGroupHooksUrl(host, ownerPath, null);
        var payload = JsonSerializer.Serialize(BuildGroupHookUpsert(request));

        // GitLab takes a second hook at a URL a hook already has, and a dropped connection or a timeout on this raw call
        // is transient: a blind re-send of a create that landed leaves two hooks on the group. A retry first lists the
        // group's hooks for the one at this registration's callback URL, which carries the row's own id. The create's
        // answer is judged outside the wrapper: a 4xx/5xx is the registration's failure, not retried.
        var answer = await _resilience.ExecuteNonIdempotentAsync(context.Instance, nameof(RegisterConnectionWebhookAsync),
            _ => SendGroupHookRequestAsync(HttpMethod.Post, url, token, payload, cancellationToken),
            _ => FindLandedGroupHookAsync(url, token, request.CallbackUrl, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        var created = JsonSerializer.Deserialize<GitLabGroupHook>(EnsureAccepted(answer, CaptureGroupHookRequest("POST", url, token, payload)).Body, _snakeCaseJson);

        if (created == null)
            throw new InvalidOperationException($"GitLab accepted the group hook on {ownerPath} but answered a body we could not read");

        return new RemoteWebhook { ExternalId = created.Id.ToString(), CallbackUrl = request.CallbackUrl, SubscribedEvents = request.SubscribedEvents.ToList(), Active = true };
    }

    public async Task DeleteConnectionWebhookAsync(ProviderContext context, string ownerPath, string externalWebhookId, CancellationToken cancellationToken)
    {
        var (_, host, token) = await BuildAuthedAsync(context, cancellationToken).ConfigureAwait(false);
        var url = BuildGroupHooksUrl(host, ownerPath, externalWebhookId);

        var answer = await _resilience.ExecuteAsync(context.Instance, nameof(DeleteConnectionWebhookAsync), _ => SendGroupHookRequestAsync(HttpMethod.Delete, url, token, null, cancellationToken), cancellationToken).ConfigureAwait(false);

        EnsureAccepted(answer, CaptureGroupHookRequest("DELETE", url, token, null));
    }

    /// <summary>
    /// One place that decides what a refusal means. Every exit carries the request we sent and the
    /// answer we got, because for this endpoint the answer IS the diagnosis — a Free instance and a
    /// wrongly-scoped token both answer 403, and only GitLab's own words separate them.
    /// </summary>
    private static GroupHookAnswer EnsureAccepted(GroupHookAnswer answer, CapturedProviderRequest request)
    {
        if (answer.Status is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices) return answer;

        throw DescribeGroupHookRefusal(answer, request);
    }

    /// <summary>
    /// The hook an earlier attempt of this create landed, answered the way the create would have
    /// answered — or null when none did. Read raw, inside the create's attempt: a list GitLab
    /// refuses fails the attempt with the list's own answer rather than reading as "nothing
    /// landed", so a probe that cannot tell never leads to a second create.
    /// </summary>
    private static async Task<GroupHookAnswer?> FindLandedGroupHookAsync(string url, string token, string callbackUrl, CancellationToken cancellationToken)
    {
        var landed = FindGroupHook(await ListGroupHooksAsync(url, token, cancellationToken).ConfigureAwait(false), callbackUrl);

        return landed == null ? null : new GroupHookAnswer(HttpStatusCode.OK, JsonSerializer.Serialize(landed, _snakeCaseJson));
    }

    /// <summary>
    /// Every hook on the group — the one list both the registrar's idempotency check and a retried create's probe read.
    /// GitLab answers 20 to a page unless asked for more, and an unpaged read never saw a hook past the twentieth: in a
    /// busier group both lookups missed this registration's hook, and a retry created a second one. So the list asks
    /// for GitLab's largest page and follows <c>X-Next-Page</c> forward until GitLab names no later page. Every page is
    /// judged like any other answer: a refused page leaves the list unknown, never shorter.
    /// </summary>
    private static async Task<List<GitLabGroupHook>> ListGroupHooksAsync(string url, string token, CancellationToken cancellationToken)
    {
        var hooks = new List<GitLabGroupHook>();
        int? page = 1;

        while (page is { } current)
        {
            var pageUrl = $"{url}?per_page={GroupHooksPageSize}&page={current}";
            var answer = EnsureAccepted(await SendGroupHookRequestAsync(HttpMethod.Get, pageUrl, token, null, cancellationToken).ConfigureAwait(false), CaptureGroupHookRequest("GET", pageUrl, token, null));

            hooks.AddRange(JsonSerializer.Deserialize<List<GitLabGroupHook>>(answer.Body, _snakeCaseJson) ?? new List<GitLabGroupHook>());
            page = NextPageAfter(current, answer.NextPage);
        }

        return hooks;
    }

    /// <summary>
    /// The page to read after <paramref name="current"/>, or null when it was the last. Only a later page counts: empty or
    /// absent is GitLab's own last page, and a value that does not advance — a proxy or a GitLab bug repeating a page — is
    /// read the same way, so the list can never go round forever.
    /// </summary>
    private static int? NextPageAfter(int current, string? nextPage) =>
        int.TryParse(nextPage, out var next) && next > current ? next : null;

    private static async Task<GroupHookAnswer> SendGroupHookRequestAsync(HttpMethod method, string url, string token, string? payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);

        if (payload != null) request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        request.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", token);

        using var response = await _countsHttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return new GroupHookAnswer(response.StatusCode, body, ReadNextPage(response));
    }

    /// <summary>GitLab's <c>X-Next-Page</c>: the next page's number on a list, empty on its last page, absent on anything else.</summary>
    private static string? ReadNextPage(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-Next-Page", out var values) ? values.FirstOrDefault() : null;

    /// <summary>
    /// Which refusal this is.
    ///
    /// <para>403 is the plan one: the endpoint exists, the instance understood the call, and the
    /// tier does not include it. Saying so matters because a bare 403 reads as a token problem and
    /// sends an operator to re-issue a credential that was never wrong — and the remedy names the
    /// way out that costs nothing, because otherwise the only remedy an operator sees is "pay".</para>
    ///
    /// <para>404 is deliberately NOT the plan one. GitLab answers 404 for a group that does not
    /// exist, for a group this token cannot see, AND for a namespace that is a USER rather than a
    /// group — which has no group hooks at any tier. Calling all of those "needs Premium" would send
    /// somebody to buy a licence that cannot help them, so the message names the possibilities
    /// instead of picking the expensive one.</para>
    /// </summary>
    private static Exception DescribeGroupHookRefusal(GroupHookAnswer answer, CapturedProviderRequest request)
    {
        var status = (int)answer.Status;
        var diagnostic = new ProviderCallDiagnostic { StatusCode = status, ResponseBody = ProviderCallCapture.Clamp(answer.Body), Request = request };
        var evidence = new ProviderWebhookRegistrationException(diagnostic, new InvalidOperationException($"GitLab answered HTTP {status} for {request.Method} {request.Url}"));

        if (answer.Status != HttpStatusCode.Forbidden) return evidence;

        return new ProviderPlanRequirementException(ProviderKind.GitLab, GroupHookFeature, GroupHookPlan, status, GroupHookRemedy, evidence);
    }

    /// <summary>The sentence an operator can act on without spending money. Pinned by the registration-flow test, because a refusal that names only the paid way out is a refusal that reads as a dead end.</summary>
    private const string GroupHookRemedy = "Either upgrade the group, or leave this connection on per-repository webhook scope, which registers one hook per bound repository and needs no group-level plan.";

    /// <summary>GitLab addresses a nested group by URL-encoded full path; a raw slash is a different route.</summary>
    private static string BuildGroupHooksUrl(string host, string ownerPath, string? hookId)
    {
        var root = $"{host.TrimEnd('/')}/api/v4/groups/{Uri.EscapeDataString(ownerPath)}/hooks";

        return hookId == null ? root : $"{root}/{hookId}";
    }

    private static CapturedProviderRequest CaptureGroupHookRequest(string method, string url, string token, string? body)
    {
        var headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" };

        return ProviderCallCapture.CaptureRedacted(method, url, headers, body, new[] { token });
    }

    private static RemoteWebhook? MatchGroupHookByCallbackUrl(IEnumerable<GitLabGroupHook> hooks, string callbackUrl)
    {
        var match = FindGroupHook(hooks, callbackUrl);

        if (match == null) return null;

        return new RemoteWebhook { ExternalId = match.Id.ToString(), CallbackUrl = match.Url ?? callbackUrl, SubscribedEvents = ReadSubscribedEvents(match), Active = true };
    }

    /// <summary>The listed hook at <paramref name="callbackUrl"/> — the one match behind the registrar's idempotency check and a retried create's probe.</summary>
    private static GitLabGroupHook? FindGroupHook(IEnumerable<GitLabGroupHook> hooks, string callbackUrl) =>
        hooks.FirstOrDefault(h => string.Equals(h.Url, callbackUrl, StringComparison.OrdinalIgnoreCase));

    private static List<string> ReadSubscribedEvents(GitLabGroupHook hook) =>
        GitLabHookEvents.Names(hook.PushEvents, hook.MergeRequestsEvents, hook.IssuesEvents);

    /// <summary>Same boolean-per-event shape the project endpoint takes, which is why the mapping reads the same as <c>BuildHookUpsert</c>.</summary>
    /// <summary>
    /// Every event a group hook can carry, subscribed. See <see cref="GitLabHookEvents.GroupHookAttributes"/>
    /// for why the set is the provider's rather than the three this system reads. Built as a
    /// dictionary because the attribute list is data, and an anonymous type would have to restate it
    /// as sixteen hand-written properties that can drift from it.
    /// </summary>
    private static object BuildGroupHookUpsert(WebhookRegistration request)
    {
        var body = new Dictionary<string, object>
        {
            ["url"] = request.CallbackUrl,
            ["token"] = request.Secret,
            ["enable_ssl_verification"] = true
        };

        foreach (var attribute in GitLabHookEvents.GroupHookAttributes) body[attribute] = true;

        return body;
    }

    private sealed record GroupHookAnswer(HttpStatusCode Status, string Body, string? NextPage = null);

    private sealed record GitLabGroupHook
    {
        public long Id { get; init; }
        public string? Url { get; init; }
        public bool PushEvents { get; init; }
        public bool MergeRequestsEvents { get; init; }
        public bool IssuesEvents { get; init; }
    }
}
