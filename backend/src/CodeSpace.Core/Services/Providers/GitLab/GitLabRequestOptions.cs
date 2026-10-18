using System.Net;
using CodeSpace.Core.Services.OutboundHttp;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using NGitLab;

namespace CodeSpace.Core.Services.Providers.GitLab;

/// <summary>
/// How NGitLab 11.7.0 sends a request, which it does on <see cref="HttpWebRequest"/> and exposes only through
/// <see cref="RequestOptions"/>. Two things are wrong with its own sending, and both are set right here, on the request it
/// hands over just before it is sent.
///
/// <para>The header. NGitLab writes <c>Authorization: Bearer &lt;token&gt;</c> whatever the token is; <see
/// cref="GitLabCredential.Attach(WebHeaderCollection)"/> replaces it with the one header the credential's type names.</para>
///
/// <para>The redirect. Left to <see cref="HttpWebRequest"/> a 301 is followed with the <c>Authorization</c> header dropped on
/// every hop, so the call arrives anonymous and fails with a bare 401. Auto-redirect is turned off and the hop is followed
/// here instead: a GET or HEAD whose redirect stays inside one origin (<see cref="OriginScopedRedirectHandler.IsSameOrigin"/>)
/// is re-sent with the same headers, at most <see cref="OriginScopedRedirectHandler.MaxRedirects"/> times. Anything else — a
/// redirect to another origin, to a request that changes something (NGitLab has written its body to a stream it does not hand
/// back, so it cannot be re-sent, and a create the first answer may already have landed must not be repeated), or one that does
/// not end — is not followed and fails with <see cref="ProviderBaseUrlRedirectsException"/> naming where GitLab pointed.</para>
///
/// <para>The origin. NGitLab builds a request for whatever URL a paged answer names (<c>Link: &lt;url&gt;; rel="next"</c>), and
/// hands it over here like any other, so the credential would follow a page link to any origin the answer chose. A request
/// to another origin than the connection's is refused before the credential is attached, with the same
/// <see cref="ProviderBaseUrlRedirectsException"/>: nothing is sent there.</para>
///
/// <para>Construct with <c>(0, TimeSpan.Zero)</c>, what <see cref="RequestOptions.Default"/> is: no retries of its own — the
/// provider's resilience wrapper retries — and its 5-minute timeout.</para>
/// </summary>
internal sealed class GitLabRequestOptions : RequestOptions
{
    private readonly GitLabCredential _credential;
    private readonly Uri? _connection;

    /// <param name="credential">What every request presents, in the one header its type names.</param>
    /// <param name="host">The address the connection's calls go to; its origin is the only one a request may name. Not an absolute URL: NGitLab could not have built a request from it either, so none is checked.</param>
    public GitLabRequestOptions(GitLabCredential credential, string host) : base(0, TimeSpan.Zero)
    {
        _credential = credential;
        _connection = Uri.TryCreate(host.Trim(), UriKind.Absolute, out var parsed) ? parsed : null;
    }

    public override WebResponse GetResponse(HttpWebRequest request)
    {
        var current = Prepare(request);

        for (var hop = 0; ; hop++)
        {
            var response = (HttpWebResponse)current.GetResponse();

            if (FollowTarget(current, response, hop) is not { } next) return response;

            response.Dispose();
            current = next;
        }
    }

    public override async Task<WebResponse> GetResponseAsync(HttpWebRequest request, CancellationToken cancellationToken)
    {
        var current = Prepare(request);

        for (var hop = 0; ; hop++)
        {
            var response = await SendAsync(current, cancellationToken).ConfigureAwait(false);

            if (FollowTarget(current, response, hop) is not { } next) return response;

            response.Dispose();
            current = next;
        }
    }

    /// <summary>One request, aborted with its caller's token — as the base class sends it.</summary>
    private static async Task<HttpWebResponse> SendAsync(HttpWebRequest request, CancellationToken cancellationToken)
    {
        using (cancellationToken.CanBeCanceled ? cancellationToken.Register(request.Abort) : default)
        {
            return (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The request as it is sent: on the connection's origin, its redirects left to us, and the credential in the one header its type names.</summary>
    private HttpWebRequest Prepare(HttpWebRequest request)
    {
        EnsureOnTheConnectionsOrigin(request.RequestUri);

        request.AllowAutoRedirect = false;
        _credential.Attach(request.Headers);

        return request;
    }

    private void EnsureOnTheConnectionsOrigin(Uri target)
    {
        if (_connection is null || OriginScopedRedirectHandler.IsSameOrigin(_connection, target)) return;

        throw new ProviderBaseUrlRedirectsException(ProviderKind.GitLab, _connection, target);
    }

    /// <summary>
    /// The request that follows <paramref name="response"/>, or null when it is not a redirect (it is the answer). A redirect
    /// that is not followed throws rather than returning, so the 3xx never reaches NGitLab to be read as a body.
    /// </summary>
    private static HttpWebRequest? FollowTarget(HttpWebRequest sent, HttpWebResponse response, int hop)
    {
        if (!OriginScopedRedirectHandler.IsRedirect(response.StatusCode) || !Uri.TryCreate(sent.RequestUri, response.Headers["Location"], out var target)) return null;

        var followable = IsReadOnly(sent.Method) && OriginScopedRedirectHandler.IsSameOrigin(sent.RequestUri, target) && hop < OriginScopedRedirectHandler.MaxRedirects;

        if (!followable)
        {
            response.Dispose();

            throw new ProviderBaseUrlRedirectsException(ProviderKind.GitLab, sent.RequestUri, target);
        }

        return Follow(sent, target);
    }

    private static bool IsReadOnly(string method) => method is "GET" or "HEAD";

    /// <summary>
    /// The request at <paramref name="target"/> carrying what NGitLab set on <paramref name="sent"/>. <see cref="HttpWebRequest"/>
    /// is obsolete, and it is the one transport NGitLab 11.7.0 speaks and hands to <see cref="RequestOptions"/>: the next hop
    /// has to be the same kind of request, so the warning is silenced for this one line rather than the file.
    /// </summary>
    private static HttpWebRequest Follow(HttpWebRequest sent, Uri target)
    {
#pragma warning disable SYSLIB0014
        var next = WebRequest.CreateHttp(target);
#pragma warning restore SYSLIB0014

        next.Method = sent.Method;
        next.Accept = sent.Accept;
        next.Headers = sent.Headers;
        next.AutomaticDecompression = sent.AutomaticDecompression;
        next.Timeout = sent.Timeout;
        next.ReadWriteTimeout = sent.ReadWriteTimeout;
        next.Proxy = sent.Proxy;
        next.AllowAutoRedirect = false;

        return next;
    }
}
