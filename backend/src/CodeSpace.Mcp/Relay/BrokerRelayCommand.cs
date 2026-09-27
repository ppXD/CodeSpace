using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace CodeSpace.Mcp.Relay;

/// <summary>
/// The parsed argv of <c>codespace-mcp relay &lt;port&gt; &lt;socket&gt; -- &lt;cli&gt; [args…]</c>. Pure: every malformed
/// shape throws an <see cref="ArgumentException"/> carrying the usage line, which <see cref="BrokerRelay"/> turns into
/// the proxy's own usage exit code before anything is bound or started. The socket path is validated here, as the
/// endpoint the relay connects to, so a path too long for a Unix socket address fails at startup rather than as a
/// reset on the CLI's first call.
/// </summary>
internal sealed record BrokerRelayCommand(int Port, UnixDomainSocketEndPoint Broker, string Cli, IReadOnlyList<string> CliArgs)
{
    internal const string Usage = "usage: codespace-mcp relay <port> <socket> -- <cli> [args...]";

    private const string Separator = "--";

    internal static BrokerRelayCommand Parse(IReadOnlyList<string> args)
    {
        if (args.Count < 4 || args[2] != Separator)
            throw new ArgumentException(Usage);

        return new BrokerRelayCommand(ParsePort(args[0]), ParseBroker(args[1]), ParseCli(args[3]), args.Skip(4).ToArray());
    }

    private static int ParsePort(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > IPEndPoint.MinPort and <= IPEndPoint.MaxPort
            ? port
            : throw new ArgumentException($"'{value}' is not a TCP port (1-65535). {Usage}");

    private static UnixDomainSocketEndPoint ParseBroker(string path)
    {
        if (string.IsNullOrEmpty(path))
            throw new ArgumentException($"The broker socket path is empty. {Usage}");

        try { return new UnixDomainSocketEndPoint(path); }
        catch (ArgumentOutOfRangeException) { throw new ArgumentException($"The broker socket path '{path}' is too long for a Unix socket address. {Usage}"); }
    }

    private static string ParseCli(string cli) => string.IsNullOrEmpty(cli) ? throw new ArgumentException($"The CLI to run is empty. {Usage}") : cli;
}
