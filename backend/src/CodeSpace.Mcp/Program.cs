using System.Net.Sockets;
using CodeSpace.Mcp;

// The `codespace mcp --proxy` forwarder: a tiny, zero-dependency stdio<->UDS bridge the CLI harness launches INSIDE
// the sandbox. It reads the run's socket path + token from the env (the runner stages them before exec), connects the
// per-run Unix-domain socket, authenticates by sending the token as the first line, then forwards raw bytes both ways
// until either side closes. No project references — re-entering the backend would drag Kestrel/EF/DB-creds into a
// sandbox-exec'able binary; this stays a dumb byte pump that parses nothing.
try
{
    var (socketPath, token) = McpProxyEnv.ResolveConfig(args, Environment.GetEnvironmentVariables());

    using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));

    await using var net = new NetworkStream(socket, ownsSocket: false);

    await McpProxyPump.AuthenticateAsync(net, token, CancellationToken.None);

    using var stdin = Console.OpenStandardInput();
    using var stdout = Console.OpenStandardOutput();

    await McpProxyPump.ForwardAsync(stdin, stdout, net, CancellationToken.None);

    return 0;
}
catch (ArgumentException ex)
{
    // A missing/empty socket or token is a usage error — fail closed and loud (never connect anonymously, never no-op).
    await Console.Error.WriteLineAsync(ex.Message);
    return 2;
}
