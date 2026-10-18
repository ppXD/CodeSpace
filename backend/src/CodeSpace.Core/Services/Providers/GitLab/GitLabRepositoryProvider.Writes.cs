using System.Net;
using NGitLab;

namespace CodeSpace.Core.Services.Providers.GitLab;

/// <summary>
/// A write through NGitLab's typed client, sent again when GitLab refused its token. <see cref="GitLabRequestOptions"/>
/// answers a 401 to an OAuth credential by asking for a replacement, and for a read sends the request again with it; a write it
/// cannot (NGitLab wrote the body to a stream it does not hand back), so it settles the credential and lets the 401 stand. That left
/// the first merge, comment or issue after another consumer rotated the pair failing with the very 401 recovery exists
/// for, whichever call came first — a read before it settles the token, and a write that opens a call is the first request.
///
/// <para>The provider can send it: a 401 is GitLab's answer before it acts on a request, so repeating the call repeats
/// nothing, and the credential the client presents already carries the replacement. Once — a replacement refused too is the
/// answer.</para>
/// </summary>
public sealed partial class GitLabRepositoryProvider
{
    /// <summary>
    /// <paramref name="write"/>, made again once if GitLab answered 401 and the refusal replaced the credential's token — the
    /// token this attempt began with is no longer the one the credential carries. A 401 that left the token as it was (a static
    /// token, or no replacement to offer) stands, and so does any other failure.
    /// </summary>
    private static Func<CancellationToken, Task<T>> ResentOnceIfRefused<T>(GitLabCredential credential, Func<CancellationToken, Task<T>> write) => async cancellationToken =>
    {
        var presented = credential.Token;

        try
        {
            return await write(cancellationToken).ConfigureAwait(false);
        }
        catch (GitLabException refusal) when (refusal.StatusCode == HttpStatusCode.Unauthorized && credential.Token != presented)
        {
            return await write(cancellationToken).ConfigureAwait(false);
        }
    };

    /// <summary><see cref="ResentOnceIfRefused{T}"/> for a write that returns nothing.</summary>
    private static Func<CancellationToken, Task> ResentOnceIfRefused(GitLabCredential credential, Func<CancellationToken, Task> write)
    {
        var resending = ResentOnceIfRefused<object?>(credential, async cancellationToken =>
        {
            await write(cancellationToken).ConfigureAwait(false);

            return null;
        });

        return resending;
    }
}
