using System.Net;

namespace CodeSpace.Core.Services.OutboundHttp;

/// <summary>
/// Follows redirects for a guarded client in place of the handler's built-in follower, which strips only
/// <c>Authorization</c> when a redirect changes origin: every other header the author wrote — <c>X-Api-Key</c>,
/// <c>PRIVATE-TOKEN</c>, <c>Cookie</c>, often resolved from a team secret — went on to whatever host the first one
/// pointed at. Here a hop that changes scheme, host or port drops EVERY request header; a same-origin hop keeps them,
/// since that is the origin they were written for. That includes an http→https move on the same host: it arrives
/// without the author's headers, and the author keeps them by writing the https URL. A redirect that would carry the
/// request BODY to another origin (a 307/308, or a 300/301/302 of anything but a POST) is not followed at all, because
/// the body holds the same secrets — a token request's <c>client_secret</c>, a webhook's payload — and re-sending the
/// request without it would be a different request; the 3xx is returned for the workflow to act on. Each hop connects
/// through the same primary handler, so the destination guard checks it again.
///
/// <para>Followed rather than returned as a 3xx because http.request has always followed redirects, and a workflow
/// calling an API that moved behind a same-origin or body-less hop (a CDN, a canonical path) would otherwise start
/// failing. Like the built-in follower: 300/301/302/303/307/308 with a <c>Location</c> are followed; 303, and
/// 300/301/302 after a POST, continue as a body-less GET; an https→http downgrade or a non-http scheme is not followed,
/// and the 3xx is returned. Unlike it, at most <see cref="MaxRedirects"/> hops (the built-in allows 50) are followed,
/// after which the last 3xx is returned as is.</para>
/// </summary>
public sealed class OriginScopedRedirectHandler : DelegatingHandler
{
    public const int MaxRedirects = 10;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        for (var hop = 0; hop < MaxRedirects && ReadFollowableTarget(request, response) is { } target; hop++)
        {
            Retarget(request, target, response.StatusCode);
            response.Dispose();

            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }

    private static Uri? ReadFollowableTarget(HttpRequestMessage request, HttpResponseMessage response)
    {
        if (!IsRedirect(response.StatusCode) || response.Headers.Location is not { } location) return null;

        var target = location.IsAbsoluteUri ? location : new Uri(request.RequestUri!, location);

        return IsFollowable(request.RequestUri!, target) && !CarriesBodyAcrossOrigin(request, target, response.StatusCode) ? target : null;
    }

    private static bool IsRedirect(HttpStatusCode status) => (int)status is 300 or 301 or 302 or 303 or 307 or 308;

    /// <summary>https anywhere, or http from http — never a downgrade, never another scheme.</summary>
    private static bool IsFollowable(Uri current, Uri target) => target.Scheme == Uri.UriSchemeHttps || (target.Scheme == Uri.UriSchemeHttp && current.Scheme == Uri.UriSchemeHttp);

    private static bool CarriesBodyAcrossOrigin(HttpRequestMessage request, Uri target, HttpStatusCode status) => request.Content != null && !ContinuesAsGet(status, request.Method) && !IsSameOrigin(request.RequestUri!, target);

    private static void Retarget(HttpRequestMessage request, Uri target, HttpStatusCode status)
    {
        if (!IsSameOrigin(request.RequestUri!, target)) request.Headers.Clear();

        if (ContinuesAsGet(status, request.Method)) DropBody(request);

        request.RequestUri = target;
    }

    private static bool IsSameOrigin(Uri current, Uri target) => current.Scheme == target.Scheme && current.Port == target.Port && string.Equals(current.IdnHost, target.IdnHost, StringComparison.OrdinalIgnoreCase);

    private static bool ContinuesAsGet(HttpStatusCode status, HttpMethod method) => status == HttpStatusCode.SeeOther || ((int)status is 300 or 301 or 302 && method == HttpMethod.Post);

    private static void DropBody(HttpRequestMessage request)
    {
        request.Method = HttpMethod.Get;
        request.Content?.Dispose();
        request.Content = null;
    }
}
