using System.Text;

namespace CodeSpace.Mcp;

/// <summary>
/// The transport-transparent byte forwarder at the heart of the proxy: after authenticating (writing the run token as
/// the connection's first line), it runs two raw byte-copy loops full-duplex — stdin→socket and socket→stdout — and
/// the first EOF cancels the other. It does NOT parse JSON-RPC, does NOT buffer-on-newline, does NOT re-encode: the
/// newline framing is purely the server's (<c>McpFramingLoop</c>) and the harness's concern, so the proxy just
/// forwards bytes as they arrive. Pure over the streams it's handed (no process / Console concern) so a unit test
/// drives it with an in-memory connected socket pair + memory streams without spawning.
/// </summary>
internal static class McpProxyPump
{
    private const int BufferSize = 16 * 1024;

    private static readonly Encoding Ascii = Encoding.ASCII;

    /// <summary>Write the run token as the connection's first line (ASCII, '\n'-terminated), flushing it before any byte forwarding so the server authenticates before serving.</summary>
    internal static async Task AuthenticateAsync(Stream net, string token, CancellationToken ct)
    {
        var line = Ascii.GetBytes(token + "\n");

        await net.WriteAsync(line, ct).ConfigureAwait(false);
        await net.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Run the two copy loops full-duplex; the first to reach EOF cancels the other so neither side hangs once a direction closes.</summary>
    internal static async Task ForwardAsync(Stream stdin, Stream stdout, Stream net, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var up = PumpAsync(stdin, net, cts);
        var down = PumpAsync(net, stdout, cts);

        await Task.WhenAny(up, down).ConfigureAwait(false);

        cts.Cancel();

        try { await Task.WhenAll(up, down).ConfigureAwait(false); }
        catch { /* a cancelled pump unwinds as OCE/IOException — expected once one direction closed */ }
    }

    private static async Task PumpAsync(Stream from, Stream to, CancellationTokenSource cts)
    {
        var buffer = new byte[BufferSize];

        try
        {
            int n;
            while ((n = await from.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, n), cts.Token).ConfigureAwait(false);
                await to.FlushAsync(cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* the other direction closed and cancelled us */ }
        catch (IOException) { /* a torn-down stream mid-copy */ }
        finally { cts.Cancel(); }
    }
}
