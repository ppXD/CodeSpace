namespace CodeSpace.Api.Logging;

/// <summary>
/// Caps how long a single batch post to Seq may take.
///
/// <para>A Seq that refuses the connection fails immediately and costs nothing. One that ACCEPTS the socket and
/// then never answers is the expensive case: the post waits on the HTTP client's own timeout, and
/// <c>Log.CloseAndFlush</c> inherits that wait on the way out — measured at roughly two hundred seconds, which
/// reads as a hung shutdown rather than a logging problem.</para>
///
/// <para>The sink's own overload takes an <c>HttpMessageHandler</c> but not a timeout, and the response timeout
/// lives on <c>HttpClient</c> rather than the handler, so it cannot be set from there. A handler that cancels its
/// own send is the way to bound it from where we are standing.</para>
///
/// <para>Two seconds because a batch of logs is worth about that much: past it the process is being held open by
/// the thing that writes ABOUT the shutdown rather than by the shutdown. A dropped batch is the correct trade —
/// the console already has every line.</para>
/// </summary>
public sealed class BoundedSeqPostHandler : DelegatingHandler
{
    private static readonly TimeSpan PostTimeout = TimeSpan.FromSeconds(2);

    public BoundedSeqPostHandler() : base(new HttpClientHandler()) { }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        deadline.CancelAfter(PostTimeout);

        return await base.SendAsync(request, deadline.Token).ConfigureAwait(false);
    }
}
