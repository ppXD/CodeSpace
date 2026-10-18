using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Failures;

namespace CodeSpace.Messages.Exceptions;

/// <summary>
/// Thrown when a git provider's base URL answers a redirect that was not followed — above all one to another ORIGIN: a
/// different scheme, host or port (http to https, a host alias, a reverse proxy on another port, an <c>external_url</c> that
/// disagrees with the base URL). A redirect inside one origin is followed with the credential attached again; one that
/// changes origin is not, because a connected token is only ever sent to the origin it was connected for and the new origin
/// was named by whoever answered, not by the member who connected it. Left to a client's own redirect follower the credential
/// went missing (a typed client dropped its <c>Authorization</c> and arrived anonymous) or went along (a <c>PRIVATE-TOKEN</c>
/// kept on a hop to another host), and the failure read as a bare 401 that sent the owner to re-issue a token that was never
/// wrong. It names the URL the provider pointed at instead: the owner edits the base URL to it — an edit that only upgrades
/// http to https on the same host is allowed while tokens are connected — or removes the redirect.
///
/// <para>The same failure covers the two redirects inside one origin that are not followed either: one answered to a
/// request that changes something (a request is never sent again blindly, so a create the first answer may already have
/// landed is not repeated) and one that does not end. And it covers the next page of a list when the answer names another
/// origin for it (<c>Link: rel="next"</c>), which is as much the answering server's to write as a <c>Location</c>.</para>
///
/// <para>git's own transport follows a redirect itself and then asks for a credential at the origin it was sent to, finding
/// none; the command's failure is named with this type too, built from the two origins alone
/// (<see cref="ProviderBaseUrlRedirectsException(Uri, Uri)"/>), because git does not know which provider a remote belongs to.</para>
///
/// <para>Both URLs are scheme, host, port and path alone: the redirect's <c>Location</c> is the redirecting server's to write,
/// and it may carry userinfo or a query string with a credential in it, which this message, its details and the logs never
/// repeat.</para>
/// </summary>
public sealed class ProviderBaseUrlRedirectsException : Exception, IFailure
{
    public FailureKind Kind => FailureKind.Unprocessable;

    public string Code => FailureCodes.ProviderBaseUrlRedirects;

    public IReadOnlyDictionary<string, object?>? Details => ProviderKind is { } provider
        ? new Dictionary<string, object?> { ["provider"] = provider.ToString(), ["requestedOrigin"] = RequestedOrigin, ["finalUrl"] = FinalUrl, ["finalOrigin"] = FinalOrigin }
        : new Dictionary<string, object?> { ["requestedOrigin"] = RequestedOrigin, ["finalUrl"] = FinalUrl, ["finalOrigin"] = FinalOrigin };

    /// <summary>The provider whose address redirected; null when the redirect was met by git's own transport, which does not know it.</summary>
    public ProviderKind? ProviderKind { get; }

    /// <summary>Scheme, host and port (when not the scheme's own) of the URL the call was made to — the origin the credential was connected for.</summary>
    public string RequestedOrigin { get; }

    /// <summary>Where the provider pointed: scheme, host, port and path, never userinfo, query or fragment.</summary>
    public string FinalUrl { get; }

    /// <summary>Scheme, host and port of <see cref="FinalUrl"/> — what the provider's base URL would be edited to.</summary>
    public string FinalOrigin { get; }

    public ProviderBaseUrlRedirectsException(ProviderKind providerKind, Uri requested, Uri redirectsTo) : this((ProviderKind?)providerKind, requested, redirectsTo)
    {
    }

    /// <summary>A redirect git met, for a remote whose provider is not known here: <paramref name="requested"/> is the remote, <paramref name="redirectsTo"/> the origin git was sent to and found no credential for.</summary>
    public ProviderBaseUrlRedirectsException(Uri requested, Uri redirectsTo) : this(null, requested, redirectsTo)
    {
    }

    private ProviderBaseUrlRedirectsException(ProviderKind? providerKind, Uri requested, Uri redirectsTo) : base(BuildMessage(providerKind, requested, redirectsTo))
    {
        ProviderKind = providerKind;
        RequestedOrigin = OriginOf(requested);
        FinalUrl = Sanitised(redirectsTo);
        FinalOrigin = OriginOf(redirectsTo);
    }

    private static string BuildMessage(ProviderKind? provider, Uri requested, Uri redirectsTo)
    {
        var host = provider?.ToString() ?? "the git host";
        var subject = char.ToUpperInvariant(host[0]) + host[1..];
        var edit = $"Edit the provider's base URL to the address {host} answers on ({OriginOf(redirectsTo)}), or remove the redirect on {host}'s side.";

        return OriginOf(requested) == OriginOf(redirectsTo)
            ? $"{subject} answered a redirect from {Sanitised(requested)} to {Sanitised(redirectsTo)} that was not followed: the request changes something, or the redirects do not end, and a request is never sent again blindly. {edit}"
            : $"{subject} answered a request to {OriginOf(requested)} with a redirect or link to {Sanitised(redirectsTo)}, which is another origin (scheme, host or port). A connected token is only ever sent to the origin it was connected for, so it was not followed. {edit} An edit that only upgrades http to https on the same host is allowed while tokens are connected.";
    }

    /// <summary><c>scheme://host</c>, or <c>scheme://host:port</c> when the port is not the scheme's own — never userinfo, path or query.</summary>
    public static string OriginOf(Uri uri) => $"{uri.Scheme}://{HostOf(uri)}{(uri.IsDefaultPort ? "" : ":" + uri.Port)}";

    /// <summary>The URL as <c>scheme://host[:port]/path</c> — no userinfo, query or fragment, which may carry a credential.</summary>
    public static string Sanitised(Uri uri) => OriginOf(uri) + uri.AbsolutePath;

    private static string HostOf(Uri uri) => uri.HostNameType == UriHostNameType.IPv6 ? $"[{uri.IdnHost.Trim('[', ']')}]" : uri.IdnHost;
}
