using System.Net.Sockets;

namespace CodeSpace.Core.Services.OutboundHttp;

/// <summary>
/// The per-request half of the destination guard (the connect-time half is <see cref="OutboundDestinationGuard.ConnectAsync"/>),
/// run on every hop, redirects included.
///
/// <para>A request is addressed to the authority in its URL, the one the guard checks: an author-written <c>Host</c>
/// header is dropped, because <c>Host</c> — and the TLS SNI the handler derives from it — is what picks the virtual host
/// behind an address, so an operator who admits one name on a shared ingress would otherwise have admitted every other
/// name behind it. Browsers' <c>fetch</c> treats <c>Host</c> as a forbidden request header the same way.</para>
///
/// <para>A request the operator's proxy will carry has its destination checked here, before it is sent: the socket the
/// handler opens goes to the proxy, so the connect-time check never sees where the request is going. A refusal has the
/// shape a connect-time one has — an <see cref="HttpRequestException"/> whose inner exception is the
/// <see cref="OutboundDestinationRefusedException"/> — so callers handle the two alike.</para>
/// </summary>
public sealed class GuardedDestinationHandler : DelegatingHandler
{
    private readonly OutboundDestinationGuard _guard;

    public GuardedDestinationHandler(OutboundDestinationGuard guard) { _guard = guard; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Host = null;

        await PermitProxiedDestinationAsync(request.RequestUri!, cancellationToken).ConfigureAwait(false);

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task PermitProxiedDestinationAsync(Uri destination, CancellationToken cancellationToken)
    {
        if (_guard.ProxyFor(destination) == null) return;

        try
        {
            await _guard.PermitAsync(destination.IdnHost, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OutboundDestinationRefusedException or SocketException)
        {
            throw new HttpRequestException($"{ex.Message} ({destination.IdnHost}:{destination.Port})", ex);
        }
    }
}
