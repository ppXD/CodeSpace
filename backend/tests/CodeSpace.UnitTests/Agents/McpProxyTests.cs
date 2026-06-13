using System.Collections;
using System.Net.Sockets;
using System.Text;
using CodeSpace.Mcp;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the stdio<->UDS proxy (<see cref="McpProxyPump"/> + <see cref="McpProxyEnv"/>): it sends the run token as the
/// connection's FIRST line, forwards raw bytes both ways with no JSON-RPC parsing / no buffer-on-newline (proven with
/// a no-trailing-newline payload), and exits when EITHER side closes (no hang). <see cref="McpProxyEnv.ResolveConfig"/>
/// resolves a valid env, fails closed on a missing/empty socket or token, and its env-var NAME literals are pinned
/// (Rule 8). Tier 🟢: real production pump over a real connected <c>AF_UNIX</c> socket pair (no spawn). Skips on a host
/// without UDS support.
/// </summary>
[Trait("Category", "Unit")]
public class McpProxyTests
{
    [Fact]
    public async Task Authenticate_sends_the_token_as_the_first_line()
    {
        if (!Socket.OSSupportsUnixDomainSockets) return;

        using var pair = await SocketPair.CreateAsync();
        await using var clientNet = new NetworkStream(pair.Client, ownsSocket: false);

        await McpProxyPump.AuthenticateAsync(clientNet, "tok-123", CancellationToken.None);

        var line = await ReadLineAsync(pair.Server);
        line.ShouldBe("tok-123", customMessage: "the proxy must present the token as the first line, '\\n'-terminated");
    }

    [Fact]
    public async Task Forward_relays_bytes_both_ways_including_a_payload_with_no_trailing_newline()
    {
        if (!Socket.OSSupportsUnixDomainSockets) return;

        using var pair = await SocketPair.CreateAsync();
        await using var serverNet = new NetworkStream(pair.Server, ownsSocket: false);

        // stdin stays open (a pipe we control) so the proxy stops only when the SOCKET closes — otherwise a stdin-EOF
        // would legitimately cancel the socket→stdout direction before a late reply lands (a race, not a bug).
        var stdin = new ManualStream();
        var stdout = new MemoryStream();

        var clientPump = ForwardClientAsync(pair.Client, stdin, stdout);

        // stdin → socket (a complete JSON-RPC line) reaches the server verbatim.
        stdin.Feed(Encoding.UTF8.GetBytes("{\"id\":1}\n"));
        var fromClient = await ReadExactStringAsync(serverNet, "{\"id\":1}\n".Length);
        fromClient.ShouldBe("{\"id\":1}\n", customMessage: "stdin bytes must reach the socket verbatim");

        // socket → stdout: a reply with NO trailing newline — proves the pump forwards bytes as they arrive.
        var reply = Encoding.UTF8.GetBytes("{\"result\":\"no-newline\"}");
        await serverNet.WriteAsync(reply);
        await serverNet.FlushAsync();

        await WaitUntilAsync(() => stdout.ToArray().Length >= reply.Length, TimeSpan.FromSeconds(5));

        Encoding.UTF8.GetString(stdout.ToArray()).ShouldBe("{\"result\":\"no-newline\"}", customMessage: "a reply with no trailing newline must still be forwarded to stdout");

        // Now close the socket → EOF cancels the proxy → it returns without hanging.
        pair.Server.Shutdown(SocketShutdown.Both);
        pair.Server.Close();

        await clientPump.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Forward_exits_when_stdin_closes()
    {
        if (!Socket.OSSupportsUnixDomainSockets) return;

        using var pair = await SocketPair.CreateAsync();

        // stdin is already at EOF (empty stream) → the up pump ends immediately → the proxy returns without hanging.
        var stdin = new MemoryStream(Array.Empty<byte>());
        var stdout = new MemoryStream();

        var clientPump = ForwardClientAsync(pair.Client, stdin, stdout);

        await Should.NotThrowAsync(async () => await clientPump.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Forward_exits_when_the_socket_closes()
    {
        if (!Socket.OSSupportsUnixDomainSockets) return;

        using var pair = await SocketPair.CreateAsync();

        // stdin stays open (a blocking pipe that never yields); the socket closing must still end the proxy.
        var stdin = new BlockingStream();
        var stdout = new MemoryStream();

        var clientPump = ForwardClientAsync(pair.Client, stdin, stdout);

        pair.Server.Shutdown(SocketShutdown.Both);
        pair.Server.Close();

        await Should.NotThrowAsync(async () => await clientPump.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void ResolveConfig_resolves_a_valid_env()
    {
        var env = new Hashtable { [McpProxyEnv.SocketEnvVar] = "/tmp/s.sock", [McpProxyEnv.TokenEnvVar] = "tok" };

        var (socketPath, token) = McpProxyEnv.ResolveConfig(Array.Empty<string>(), env);

        socketPath.ShouldBe("/tmp/s.sock");
        token.ShouldBe("tok");
    }

    [Theory]
    [InlineData(null, "tok")]      // missing socket
    [InlineData("", "tok")]        // empty socket
    [InlineData("/tmp/s.sock", null)]  // missing token
    [InlineData("/tmp/s.sock", "")]    // empty token
    public void ResolveConfig_fails_closed_on_a_missing_or_empty_value(string? socket, string? token)
    {
        var env = new Hashtable();
        if (socket is not null) env[McpProxyEnv.SocketEnvVar] = socket;
        if (token is not null) env[McpProxyEnv.TokenEnvVar] = token;

        Should.Throw<ArgumentException>(() => McpProxyEnv.ResolveConfig(Array.Empty<string>(), env));
    }

    [Fact]
    public void Env_var_name_literals_are_pinned()
    {
        // Renaming either silently breaks the runner that stages these into the proxy's env before exec (Rule 8).
        McpProxyEnv.TokenEnvVar.ShouldBe("CODESPACE_RUN_TOKEN");
        McpProxyEnv.SocketEnvVar.ShouldBe("CODESPACE_MCP_SOCKET");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Task ForwardClientAsync(Socket client, Stream stdin, Stream stdout) => Task.Run(async () =>
    {
        await using var net = new NetworkStream(client, ownsSocket: false);
        await McpProxyPump.ForwardAsync(stdin, stdout, net, CancellationToken.None);
    });

    private static async Task<string> ReadLineAsync(Socket socket)
    {
        await using var net = new NetworkStream(socket, ownsSocket: false);
        using var reader = new StreamReader(net, Encoding.UTF8);
        return await reader.ReadLineAsync() ?? "";
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition() && DateTimeOffset.UtcNow < deadline) await Task.Delay(20);

        condition().ShouldBeTrue(customMessage: "the awaited condition did not hold within the timeout");
    }

    private static async Task<string> ReadExactStringAsync(Stream stream, int count)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, count - read));
            if (n == 0) break;
            read += n;
        }

        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    /// <summary>A connected AF_UNIX socket pair over a temp-dir listener — the in-memory stand-in for the proxy↔endpoint link.</summary>
    private sealed class SocketPair : IDisposable
    {
        private readonly string _dir;
        public Socket Client { get; private init; } = null!;
        public Socket Server { get; private init; } = null!;

        private SocketPair(string dir) => _dir = dir;

        public static async Task<SocketPair> CreateAsync()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cs-proxy-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "s");

            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);

            var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            var connect = client.ConnectAsync(new UnixDomainSocketEndPoint(path));
            var server = await listener.AcceptAsync();
            await connect;

            return new SocketPair(dir) { Client = client, Server = server };
        }

        public void Dispose()
        {
            try { Client.Dispose(); } catch { /* best-effort */ }
            try { Server.Dispose(); } catch { /* best-effort */ }
            try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>A read-stream the test feeds bytes into on demand and that BLOCKS (rather than EOFing) when drained — models a stdin held open across a socket-side reply.</summary>
    private sealed class ManualStream : Stream
    {
        private readonly System.Threading.Channels.Channel<byte[]> _chunks = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        private byte[] _pending = Array.Empty<byte>();
        private int _offset;

        public void Feed(byte[] data) => _chunks.Writer.TryWrite(data);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            if (_offset >= _pending.Length)
            {
                _pending = await _chunks.Reader.ReadAsync(ct);   // blocks until fed (or cancelled)
                _offset = 0;
            }

            var n = Math.Min(buffer.Length, _pending.Length - _offset);
            _pending.AsSpan(_offset, n).CopyTo(buffer.Span);
            _offset += n;
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A read-stream that never completes — models a stdin held open while the socket side closes first.</summary>
    private sealed class BlockingStream : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
