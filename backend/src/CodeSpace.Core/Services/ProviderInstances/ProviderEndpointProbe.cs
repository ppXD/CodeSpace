using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.OutboundHttp;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.ProviderInstances;

/// <summary>
/// <c>GET {url}/api/v4/version</c> against a GitLab, redirects off for any hop that leaves the origin (a hop inside it is
/// followed, so a path that normalises on the way is not read as a verdict), no credential and no cookie — the answer's
/// status is not read at all: GitLab wants a token for that route and says 401, which is as good as 200 here. Only a 3xx
/// to another scheme, host or port counts, and it is named, not followed.
///
/// <para>Everything else — a host that is down, unresolvable, slow (the call gives up after <see cref="DefaultTimeout"/>),
/// not an http(s) URL, a provider with no known API root — is no verdict: the owner saved what they typed before this
/// existed, and a probe that blocked saves for hosts it cannot reach would be a regression for every air-gapped
/// deployment. The address is whatever the owner typed, as every provider call to it already is: no destination guard here
/// (an on-prem GitLab is on a private network), and the redirect it names is shown as scheme, host, port and path only.</para>
///
/// <para><see cref="EnsureAnswersAsync"/> asks the same host the other question, for the one edit that is allowed past
/// connected tokens — an http to https upgrade of the same host: does anything answer HTTP at the new address? The route
/// does not matter (the address as typed, no credential, no cookie) and neither does the status, a redirect included; a TLS
/// handshake that cannot complete, a host that is down or one that never answers does. There silence is the verdict, because
/// an address nothing answers at cannot be edited back while the tokens are connected.</para>
/// </summary>
public sealed class ProviderEndpointProbe : IProviderEndpointProbe, ISingletonDependency
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private const string GitLabVersionPath = "/api/v4/version";

    private static readonly HttpClient Client = BuildClient();

    private readonly TimeSpan _timeout;
    private readonly ILogger<ProviderEndpointProbe> _logger;

    public ProviderEndpointProbe(ILogger<ProviderEndpointProbe> logger) : this(logger, DefaultTimeout)
    {
    }

    internal ProviderEndpointProbe(ILogger<ProviderEndpointProbe> logger, TimeSpan timeout)
    {
        _logger = logger;
        _timeout = timeout;
    }

    public async Task EnsureNoRedirectToAnotherOriginAsync(ProviderKind provider, string url, CancellationToken cancellationToken)
    {
        if (ProbeUri(provider, url) is not { } probe) return;

        var answer = await AskAsync(probe, cancellationToken).ConfigureAwait(false);

        if (!answer.Received) _logger.LogInformation("Provider URL {Endpoint} could not be asked whether it redirects ({Reason}); it is saved as written", ProviderBaseUrlRedirectsException.OriginOf(probe), answer.Failure);

        if (answer.RedirectsTo is not { } target || OriginScopedRedirectHandler.IsSameOrigin(probe, target)) return;

        _logger.LogWarning("Provider URL {Endpoint} answers a redirect to {Target}, which is another origin", ProviderBaseUrlRedirectsException.OriginOf(probe), ProviderBaseUrlRedirectsException.Sanitised(target));

        throw new ProviderBaseUrlRedirectsException(provider, probe, target);
    }

    public async Task EnsureAnswersAsync(ProviderKind provider, string url, CancellationToken cancellationToken)
    {
        if (AnswerUri(url) is not { } address) return;

        var answer = await AskAsync(address, cancellationToken).ConfigureAwait(false);

        if (answer.Received) return;

        _logger.LogWarning("Provider URL {Endpoint} did not answer HTTP ({Reason}); an upgrade to it is refused while credentials are connected", ProviderBaseUrlRedirectsException.OriginOf(address), answer.Failure);

        throw new ProviderEndpointUnreachableException(provider, address, answer.Failure);
    }

    /// <summary>The API root probed for <paramref name="provider"/> at <paramref name="url"/>, userinfo dropped so none is sent; null when the provider has none or the URL is not an absolute http(s) one.</summary>
    private static Uri? ProbeUri(ProviderKind provider, string url)
    {
        if (provider != ProviderKind.GitLab || !Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var parsed) || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)) return null;

        return new UriBuilder(parsed) { UserName = "", Password = "", Path = parsed.AbsolutePath.TrimEnd('/') + GitLabVersionPath, Query = "", Fragment = "" }.Uri;
    }

    /// <summary>The address as typed for <see cref="EnsureAnswersAsync"/> — userinfo, query and fragment dropped so none is sent; null when it is not an absolute http(s) URL.</summary>
    private static Uri? AnswerUri(string url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var parsed) || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)) return null;

        return new UriBuilder(parsed) { UserName = "", Password = "", Path = parsed.AbsolutePath.TrimEnd('/'), Query = "", Fragment = "" }.Uri;
    }

    /// <summary>What <paramref name="probe"/> answered: whether anything did, where it redirected to (null when it did not), and, when nothing answered, why.</summary>
    private readonly record struct Answer(bool Received, Uri? RedirectsTo, string Failure);

    /// <summary>One GET of <paramref name="probe"/>. A host that cannot be reached, a handshake that cannot complete and the deadline are an <see cref="Answer"/> that was not received; the caller's own cancellation is not swallowed.</summary>
    private async Task<Answer> AskAsync(Uri probe, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, probe);
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);

            return new Answer(true, RedirectOf(probe, response), string.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new Answer(false, null, ex.GetType().Name);
        }
    }

    /// <summary>Where <paramref name="response"/> redirects to, or null when it does not.</summary>
    private static Uri? RedirectOf(Uri probe, HttpResponseMessage response)
    {
        if (!OriginScopedRedirectHandler.IsRedirect(response.StatusCode) || response.Headers.Location is not { } location) return null;

        return location.IsAbsoluteUri ? location : new Uri(probe, location);
    }

    private static HttpClient BuildClient() => new(new OriginScopedRedirectHandler { FollowsCrossOrigin = false, InnerHandler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = DefaultTimeout } }) { Timeout = Timeout.InfiniteTimeSpan };
}
