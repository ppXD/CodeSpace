using System.Text.Json;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Providers.Diagnostics;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Exceptions;
using Octokit;

namespace CodeSpace.Core.Services.Providers.GitHub;

/// <summary>
/// Organization hooks — the same three operations as the repository ones, against
/// <c>/orgs/:org/hooks</c>. The endpoint is the claim worth pinning: an organization hook covers
/// every repository under the login, and the repository endpoint would register perfectly and cover
/// one.
///
/// <para>GitHub has no nesting above the organization, so an owner path here is a single login and
/// the ancestor question the GitLab side has to answer never arises.</para>
/// </summary>
public sealed partial class GitHubRepositoryProvider : IConnectionWebhookRegistrationCapability, IWebhookRepositoryIdentifier
{
    public WebhookRepositoryIdentity? Identify(string body, IReadOnlyDictionary<string, string> headers) => _repositoryIdentifier.Identify(body, headers);

    public async Task<RemoteWebhook?> FindConnectionWebhookByCallbackUrlAsync(ProviderContext context, string ownerPath, string callbackUrl, CancellationToken cancellationToken)
    {
        var (client, baseAddress, token) = await BuildAuthedAsync(context, cancellationToken).ConfigureAwait(false);

        try
        {
            return await _resilience.ExecuteAsync(context.Instance, nameof(FindConnectionWebhookByCallbackUrlAsync), async _ =>
                MatchOrganizationHookByCallbackUrl(await client.Organization.Hook.GetAll(ownerPath).ConfigureAwait(false), callbackUrl),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Failing to LIST fails the registration exactly as surely as failing to create, and for
            // the same reasons — so the operator gets the same evidence from either step.
            throw new ProviderWebhookRegistrationException(DescribeHookFailure(ex, CaptureOrgHookRequest("GET", baseAddress, ownerPath, token, null)), ex);
        }
    }

    public async Task<RemoteWebhook> RegisterConnectionWebhookAsync(ProviderContext context, string ownerPath, WebhookRegistration request, CancellationToken cancellationToken)
    {
        var (client, baseAddress, token) = await BuildAuthedAsync(context, cancellationToken).ConfigureAwait(false);

        // Built once and used twice — as the payload and as the record of the payload — so the two
        // can never drift into describing different requests.
        var config = new Dictionary<string, string> { ["url"] = request.CallbackUrl, ["content_type"] = "json", ["secret"] = request.Secret };

        try
        {
            return await _resilience.ExecuteAsync(context.Instance, nameof(RegisterConnectionWebhookAsync), async _ =>
            {
                var newHook = new NewOrganizationHook("web", config) { Active = true, Events = GitHubHookEvents.All.ToArray() };
                var created = await client.Organization.Hook.Create(ownerPath, newHook).ConfigureAwait(false);

                return new RemoteWebhook
                {
                    ExternalId = created.Id.ToString(),
                    CallbackUrl = request.CallbackUrl,
                    SubscribedEvents = created.Events.ToList(),
                    Active = created.Active
                };
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var body = JsonSerializer.Serialize(new { name = "web", config, events = GitHubHookEvents.All, active = true });
            throw new ProviderWebhookRegistrationException(DescribeHookFailure(ex, CaptureOrgHookRequest("POST", baseAddress, ownerPath, token, body)), ex);
        }
    }

    public async Task DeleteConnectionWebhookAsync(ProviderContext context, string ownerPath, string externalWebhookId, CancellationToken cancellationToken)
    {
        var client = await BuildClientAsync(context, cancellationToken).ConfigureAwait(false);

        await _resilience.ExecuteAsync(context.Instance, nameof(DeleteConnectionWebhookAsync),
            _ => client.Organization.Hook.Delete(ownerPath, int.Parse(externalWebhookId)),
            cancellationToken).ConfigureAwait(false);
    }

    private static RemoteWebhook? MatchOrganizationHookByCallbackUrl(IEnumerable<OrganizationHook> hooks, string callbackUrl)
    {
        foreach (var hook in hooks)
        {
            if (hook.Config == null || !hook.Config.TryGetValue("url", out var url) || !string.Equals(url, callbackUrl, StringComparison.OrdinalIgnoreCase)) continue;

            return new RemoteWebhook { ExternalId = hook.Id.ToString(), CallbackUrl = url, SubscribedEvents = hook.Events.ToList(), Active = hook.Active };
        }

        return null;
    }

    /// <summary>What we sent, reproduced for an operator to read months later — Octokit carries the response but not the request. Same masking rules as the repository capture.</summary>
    private static CapturedProviderRequest CaptureOrgHookRequest(string method, Uri baseAddress, string ownerPath, string token, string? body)
    {
        var headers = new Dictionary<string, string> { ["Authorization"] = $"Token {token}" };
        var url = $"{baseAddress.ToString().TrimEnd('/')}/orgs/{ownerPath}/hooks";

        return ProviderCallCapture.CaptureRedacted(method, url, headers, body, new[] { token });
    }
}
