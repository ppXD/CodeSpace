using System.Net;
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
///
/// <para>A 401 to an OAuth credential is sent once more with the credential <see cref="GitLabCredential.RecoverAsync"/>
/// returns (a newer token another caller stored, or a rotation), by both request paths: GitLab retires an access token the
/// moment its pair is refreshed, however much of its lifetime is left, so a token read from the row an instant ago can be
/// refused. A request that carries a body is sent again too, by the raw path, which holds its body; a 401 happens before
/// GitLab acts on a request, so repeating it repeats nothing.</para>
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
    /// failure, so no call site has to know about redirects. A 401 to a credential that can be replaced
    /// (<see cref="GitLabCredential.RecoverAsync"/>) is answered by sending the request once more with the replacement; the
    /// second answer, whatever it is, is the answer.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, GitLabCredential credential, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        var replay = await CopyForReplayAsync(request, credential, cancellationToken).ConfigureAwait(false);
        var presented = credential.Token;
        var response = await SendOnceAsync(request, credential, completion, cancellationToken).ConfigureAwait(false);

        if (replay is null || response.StatusCode != HttpStatusCode.Unauthorized) return response;
        if (!await credential.RecoverAsync(presented, cancellationToken).ConfigureAwait(false)) return response;

        response.Dispose();

        return await SendOnceAsync(replay, credential, completion, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<HttpResponseMessage> SendOnceAsync(HttpRequestMessage request, GitLabCredential credential, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        var requested = request.RequestUri!;

        credential.Attach(request);

        var response = await Client.SendAsync(request, completion, cancellationToken).ConfigureAwait(false);

        if (!OriginScopedRedirectHandler.IsRedirect(response.StatusCode) || response.Headers.Location is not { } location) return response;

        var target = location.IsAbsoluteUri ? location : new Uri(response.RequestMessage!.RequestUri!, location);

        response.Dispose();

        throw new ProviderBaseUrlRedirectsException(ProviderKind.GitLab, requested, target);
    }

    /// <summary>
    /// The request as it was handed over, for sending again after a 401 — made before the first send because a redirect
    /// rewrites the request it follows. Null for a credential that cannot be replaced, which never sends twice. The body is
    /// read into memory (the raw calls' bodies are small JSON strings), which also lets the first send read it again.
    /// </summary>
    private static async Task<HttpRequestMessage?> CopyForReplayAsync(HttpRequestMessage request, GitLabCredential credential, CancellationToken cancellationToken)
    {
        if (!credential.CanRecover) return null;

        var copy = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version, VersionPolicy = request.VersionPolicy };

        foreach (var header in request.Headers) copy.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (request.Content is not { } content) return copy;

        var body = new ByteArrayContent(await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));

        foreach (var header in content.Headers) body.Headers.TryAddWithoutValidation(header.Key, header.Value);

        copy.Content = body;

        return copy;
    }

    /// <summary><see cref="SendAsync(HttpRequestMessage, GitLabCredential, HttpCompletionOption, CancellationToken)"/> reading the whole answer.</summary>
    public static Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, GitLabCredential credential, CancellationToken cancellationToken) => SendAsync(request, credential, HttpCompletionOption.ResponseContentRead, cancellationToken);
}
