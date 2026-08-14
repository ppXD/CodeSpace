using System.Net;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Providers.Diagnostics;
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

    public WebhookRepositoryIdentity? Identify(string body, IReadOnlyDictionary<string, string> headers) => _repositoryIdentifier.Identify(body, headers);

    public async Task<RemoteWebhook?> FindConnectionWebhookByCallbackUrlAsync(ProviderContext context, string ownerPath, string callbackUrl, CancellationToken cancellationToken)
    {
        var answer = await CallGroupHooksAsync(context, HttpMethod.Get, ownerPath, null, null, cancellationToken).ConfigureAwait(false);

        return MatchGroupHookByCallbackUrl(answer.Body, callbackUrl);
    }

    public async Task<RemoteWebhook> RegisterConnectionWebhookAsync(ProviderContext context, string ownerPath, WebhookRegistration request, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(BuildGroupHookUpsert(request));

        var answer = await CallGroupHooksAsync(context, HttpMethod.Post, ownerPath, null, payload, cancellationToken).ConfigureAwait(false);

        var created = JsonSerializer.Deserialize<GitLabGroupHook>(answer.Body, _snakeCaseJson);

        if (created == null)
            throw new InvalidOperationException($"GitLab accepted the group hook on {ownerPath} but answered a body we could not read");

        return new RemoteWebhook { ExternalId = created.Id.ToString(), CallbackUrl = request.CallbackUrl, SubscribedEvents = request.SubscribedEvents.ToList(), Active = true };
    }

    public async Task DeleteConnectionWebhookAsync(ProviderContext context, string ownerPath, string externalWebhookId, CancellationToken cancellationToken) =>
        await CallGroupHooksAsync(context, HttpMethod.Delete, ownerPath, externalWebhookId, null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// One call, one place that decides what a refusal means. Every exit carries the request we sent
    /// and the answer we got, because for this endpoint the answer IS the diagnosis — a Free
    /// instance and a wrongly-scoped token both answer 403, and only GitLab's own words separate them.
    /// </summary>
    private async Task<GroupHookAnswer> CallGroupHooksAsync(ProviderContext context, HttpMethod method, string ownerPath, string? hookId, string? payload, CancellationToken cancellationToken)
    {
        var (_, host, token) = await BuildAuthedAsync(context, cancellationToken).ConfigureAwait(false);
        var url = BuildGroupHooksUrl(host, ownerPath, hookId);

        var answer = await SendGroupHookRequestAsync(context, method, url, token, payload, cancellationToken).ConfigureAwait(false);

        if (answer.Status is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices) return answer;

        throw DescribeGroupHookRefusal(answer, CaptureGroupHookRequest(method.Method, url, token, payload));
    }

    private async Task<GroupHookAnswer> SendGroupHookRequestAsync(ProviderContext context, HttpMethod method, string url, string token, string? payload, CancellationToken cancellationToken)
    {
        return await _resilience.ExecuteAsync(context.Instance, nameof(CallGroupHooksAsync), async _ =>
        {
            using var request = new HttpRequestMessage(method, url);

            if (payload != null) request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            request.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", token);

            using var response = await _countsHttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return new GroupHookAnswer(response.StatusCode, body);
        }, cancellationToken).ConfigureAwait(false);
    }

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

    private static RemoteWebhook? MatchGroupHookByCallbackUrl(string listBody, string callbackUrl)
    {
        var hooks = JsonSerializer.Deserialize<List<GitLabGroupHook>>(listBody, _snakeCaseJson) ?? new List<GitLabGroupHook>();

        var match = hooks.FirstOrDefault(h => string.Equals(h.Url, callbackUrl, StringComparison.OrdinalIgnoreCase));

        if (match == null) return null;

        return new RemoteWebhook { ExternalId = match.Id.ToString(), CallbackUrl = match.Url ?? callbackUrl, SubscribedEvents = ReadSubscribedEvents(match), Active = true };
    }

    private static List<string> ReadSubscribedEvents(GitLabGroupHook hook) =>
        GitLabHookEvents.Names(hook.PushEvents, hook.MergeRequestsEvents, hook.IssuesEvents);

    /// <summary>Same boolean-per-event shape the project endpoint takes, which is why the mapping reads the same as <c>BuildHookUpsert</c>.</summary>
    private static object BuildGroupHookUpsert(WebhookRegistration request)
    {
        var flags = GitLabHookEvents.Flags(request.SubscribedEvents);

        return new
        {
            url = request.CallbackUrl,
            token = request.Secret,
            push_events = flags.Push,
            merge_requests_events = flags.MergeRequests,
            issues_events = flags.Issues,
            enable_ssl_verification = true
        };
    }

    private sealed record GroupHookAnswer(HttpStatusCode Status, string Body);

    private sealed record GitLabGroupHook
    {
        public long Id { get; init; }
        public string? Url { get; init; }
        public bool PushEvents { get; init; }
        public bool MergeRequestsEvents { get; init; }
        public bool IssuesEvents { get; init; }
    }
}
