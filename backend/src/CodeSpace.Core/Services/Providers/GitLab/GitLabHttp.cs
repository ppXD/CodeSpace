using CodeSpace.Core.Services.OutboundHttp;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using NGitLab;
using NGitLab.Impl;

namespace CodeSpace.Core.Services.Providers.GitLab;

/// <summary>
/// The one way the GitLab provider talks to GitLab: both of NGitLab's request paths and every raw HTTP call send the
/// credential in the header its type names (<see cref="GitLabCredential"/>) and follow a redirect only inside one origin
/// with that credential attached again. A redirect to another origin — http to https, a host alias, another port — is not
/// followed, so nothing is sent there, and fails with <see cref="ProviderBaseUrlRedirectsException"/> naming where GitLab
/// pointed. The raw calls' client had followed every redirect itself, keeping a <c>PRIVATE-TOKEN</c> across it.
/// </summary>
internal static class GitLabHttp
{
    /// <summary>
    /// One client for the process, as the raw calls' always was: connections are pooled, and each request carries its own
    /// credential, so one client serves every connection's token. Redirects are followed by the handler, not the primary
    /// handler, which is where a cross-origin hop is refused rather than followed. A write is followed only by a redirect that
    /// repeats it (307, 308): a POST turned into a GET by a 301 or 302 would hand back that GET's answer as the write's, so
    /// it is refused instead, as <see cref="GitLabRequestOptions"/> refuses any write NGitLab sent.
    /// </summary>
    private static readonly HttpClient Client = new(new OriginScopedRedirectHandler { FollowsCrossOrigin = false, RepeatsWritesOnly = true, InnerHandler = new SocketsHttpHandler { AllowAutoRedirect = false } });

    /// <summary>NGitLab's typed client, its requests sent as <see cref="GitLabRequestOptions"/> sends them.</summary>
    public static GitLabClient NewClient(string host, GitLabCredential credential) => new(host, credential.Token, new GitLabRequestOptions(credential, host));

    /// <summary>NGitLab's raw request builder — what a paged read is made from — sent the same way.</summary>
    public static API NewApi(string host, GitLabCredential credential) => new(new GitLabCredentials(host, credential.Token), new GitLabRequestOptions(credential, host));

    /// <summary>
    /// <paramref name="request"/> sent with <paramref name="credential"/>. A redirect that was not followed throws
    /// <see cref="ProviderBaseUrlRedirectsException"/> instead of coming back as a 3xx the caller would read as some other
    /// failure, so no call site has to know about redirects.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, GitLabCredential credential, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        var requested = request.RequestUri!;

        credential.Attach(request);

        var response = await Client.SendAsync(request, completion, cancellationToken).ConfigureAwait(false);

        if (!OriginScopedRedirectHandler.IsRedirect(response.StatusCode) || response.Headers.Location is not { } location) return response;

        var target = location.IsAbsoluteUri ? location : new Uri(response.RequestMessage!.RequestUri!, location);

        response.Dispose();

        throw new ProviderBaseUrlRedirectsException(ProviderKind.GitLab, requested, target);
    }

    /// <summary><see cref="SendAsync(HttpRequestMessage, GitLabCredential, HttpCompletionOption, CancellationToken)"/> reading the whole answer.</summary>
    public static Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, GitLabCredential credential, CancellationToken cancellationToken) => SendAsync(request, credential, HttpCompletionOption.ResponseContentRead, cancellationToken);
}
