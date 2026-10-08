using System.Net;
using System.Net.Sockets;

namespace CodeSpace.Core.Services.OutboundHttp;

/// <summary>
/// The destination check for workflow-driven outbound HTTP. As a <see cref="SocketsHttpHandler.ConnectCallback"/> it
/// resolves the host the handler is about to connect to, keeps only the addresses that are public or operator-admitted,
/// and dials EXACTLY those — so the check and the connection can never disagree about where the socket goes.
///
/// <para>Checking at connect time rather than on the URL is what makes it hold. Every spelling of an address
/// (<c>2130706433</c>, <c>0x7f000001</c>, <c>127.1</c>, <c>[::ffff:127.0.0.1]</c>) has already been reduced to the
/// address being dialled; every redirect hop opens its own connection through this same callback; and a name whose
/// DNS answer changes between a check and the connect (rebinding) has no gap to exploit, because there is one
/// resolution and it is the one dialled. A name answering with both public and internal addresses is dialled at its
/// public ones only.</para>
///
/// <para>The operator's proxy (<see cref="Proxy"/>; in production <see cref="HttpClient.DefaultProxy"/>, i.e.
/// HTTP(S)_PROXY / NO_PROXY or the OS setting — what the node's client honoured before this guard existed) is kept, so a
/// worker that reaches the internet only through it still can. A connection TO that proxy is the operator's own
/// infrastructure and is dialled unchecked; the destination of a request it carries is invisible here, so
/// <see cref="GuardedDestinationHandler"/> checks it per request through <see cref="PermitAsync"/> before it is sent. That
/// check resolves the name on the worker — a name the worker cannot resolve fails as a lookup failure, since it cannot
/// be judged — and the proxy resolves it again, so a name whose answer changes in between is the one window this guard
/// does not close; the proxy's own egress rules are the backstop there. A destination the proxy bypasses connects
/// directly, through this callback, with no such window.</para>
/// </summary>
public sealed class OutboundDestinationGuard
{
    private readonly OutboundDestinationAllowlist _allowlist;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;

    /// <param name="resolve">Name resolution; the system resolver unless a test stubs it.</param>
    public OutboundDestinationGuard(OutboundDestinationAllowlist allowlist, IWebProxy proxy, Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null)
    {
        _allowlist = allowlist;
        _resolve = resolve ?? Dns.GetHostAddressesAsync;
        Proxy = proxy;
    }

    /// <summary>The proxy the guarded handler sends through; <see cref="ProxyFor"/> applies it.</summary>
    public IWebProxy Proxy { get; }

    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var endpoint = context.DnsEndPoint;

        var addresses = IsProxyEndpoint(endpoint, context.InitialRequestMessage.RequestUri)
            ? await ResolveAsync(endpoint.Host, cancellationToken).ConfigureAwait(false)
            : await PermitAsync(endpoint.Host, cancellationToken).ConfigureAwait(false);

        return await DialAsync(addresses, endpoint.Port, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The addresses <paramref name="host"/> may be dialled at, resolved now; throws <see cref="OutboundDestinationRefusedException"/> when none is public or admitted.</summary>
    public async Task<IPAddress[]> PermitAsync(string host, CancellationToken cancellationToken) => Permit(host, await ResolveAsync(host, cancellationToken).ConfigureAwait(false));

    /// <summary>The subset of <paramref name="resolved"/> that <paramref name="host"/> may be dialled at; throws <see cref="OutboundDestinationRefusedException"/> when nothing is left.</summary>
    public IPAddress[] Permit(string host, IReadOnlyList<IPAddress> resolved)
    {
        var permitted = resolved.Where(address => PublicAddress.IsPublic(address) || _allowlist.Admits(host, address)).ToArray();

        if (permitted.Length == 0)
            throw new OutboundDestinationRefusedException(host);

        return permitted;
    }

    /// <summary>The proxy a request to <paramref name="destination"/> is sent through, or null when it connects directly — the same lookup <see cref="SocketsHttpHandler"/> makes.</summary>
    public Uri? ProxyFor(Uri destination) => Proxy.IsBypassed(destination) ? null : Proxy.GetProxy(destination);

    /// <summary>Whether this connection is the handler opening one to the proxy that carries its request, rather than to a destination.</summary>
    private bool IsProxyEndpoint(DnsEndPoint endpoint, Uri? request) => request != null && ProxyFor(request) is { } proxy && proxy.Port == endpoint.Port && string.Equals(proxy.IdnHost, endpoint.Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>An address literal (bracketed IPv6 included) is its own resolution; a name goes to DNS. A lookup failure propagates as it always did, so an unknown host still reads as one.</summary>
    private async Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal)) return new[] { literal };

        return await _resolve(host, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The handler's own default dial, restricted to the given addresses: a dual-mode socket tries each in order until one connects.</summary>
    private static async ValueTask<Stream> DialAsync(IPAddress[] addresses, int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        try
        {
            await socket.ConnectAsync(addresses, port, cancellationToken).ConfigureAwait(false);

            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
