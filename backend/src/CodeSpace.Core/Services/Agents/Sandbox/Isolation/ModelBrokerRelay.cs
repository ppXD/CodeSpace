using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using CodeSpace.Core.Services.Agents.Mcp;

namespace CodeSpace.Core.Services.Agents.Sandbox.Isolation;

/// <summary>
/// The command rewrite that puts a CLI behind the <c>codespace-mcp relay</c>: <c>&lt;helper&gt; relay &lt;port&gt;
/// &lt;socket&gt; -- &lt;command&gt; &lt;args&gt;</c>. A child whose network namespace is not the worker's cannot reach
/// its broker's loopback port, so the relay — run INSIDE whatever confines the CLI — listens on
/// <c>127.0.0.1:&lt;port&gt;</c> in the child's own namespace, the address already in the CLI's base URL, and carries
/// each connection to the lease's per-run socket. It starts the CLI itself, through <c>/bin/sh</c>'s PATH lookup, and
/// exits with its status, so the chain around it sees the CLI exactly as before. <see cref="Wrap"/> is pure, the same
/// shape as <see cref="ProcessRlimits.Wrap"/>, so the argv is testable on a host that cannot confine;
/// <see cref="HelperRunsRelay"/> asks the helper itself.
/// </summary>
public static class ModelBrokerRelay
{
    /// <summary>The helper's verb for the relay — its argv[0]. The helper's own parser owns the other end, and a unit test pins that it accepts what <see cref="Wrap"/> builds.</summary>
    public const string Verb = "relay";

    /// <summary>The status the relay exits with when it cannot listen on its port, having started nothing. The helper's own constant owns the value; a unit test pins that the two agree.</summary>
    public const int ListenFailedExitCode = 125;

    /// <summary>How long the helper has to answer <see cref="HelperRunsRelay"/>. The relay binds one socket and exits, so a helper still running past this is not the relay.</summary>
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The socket the question names. Never connected to: the relay exits before it accepts anything.</summary>
    private const string UnusedSocketPath = "/nonexistent/cs-relay-probe";

    /// <summary>The CLI the question names. Never started by a helper that answers as the relay.</summary>
    private const string UnusedCli = "/bin/true";

    /// <summary>The variables the MCP proxy connects with, kept from the question: a helper from before the relay reads any argv as the proxy's, and must fail at once rather than connect to whatever the worker's environment names.</summary>
    private static readonly string[] McpProxyConnectVariables = [McpDeclarationWriter.SocketEnvVar, McpDeclarationWriter.TokenEnvVar];

    /// <summary>The helper files that answered as the relay, by path, write time and length.</summary>
    private static readonly ConcurrentDictionary<(string Path, DateTime WrittenUtc, long Length), bool> AnsweredAsRelay = new();

    /// <summary>Rewrite <paramref name="command"/> <paramref name="args"/> to run behind the relay at <paramref name="helper"/>, listening on <paramref name="port"/> and carrying each connection to <paramref name="socketPath"/>.</summary>
    public static (string Command, IReadOnlyList<string> Args) Wrap(string helper, int port, string socketPath, string command, IReadOnlyList<string> args) =>
        (helper, [Verb, port.ToString(CultureInfo.InvariantCulture), socketPath, "--", command, .. args]);

    /// <summary>
    /// Whether the helper at <paramref name="helperPath"/> runs the relay — asked, because the file being there does not
    /// say so. The <c>CODESPACE_MCP_PROXY_PATH</c> override can name a build from before the relay, whose MCP proxy reads
    /// any argv as its own and exits with its usage error, and a file can be there and not start at all; either way a
    /// CLI put behind it never starts, and its run has already spent. The helper is started once, outside any sandbox,
    /// and told to listen on a loopback port this process already holds: the relay cannot bind it, so it exits with
    /// <see cref="ListenFailedExitCode"/> having started nothing. Only a yes is remembered, and only for the file that
    /// gave it, so a replaced helper is asked again, and a no (a fork refused under the uid's shared task cap, say) is
    /// not held against the next run.
    /// </summary>
    public static bool HelperRunsRelay(string helperPath)
    {
        if (IdentityOf(helperPath) is not { } identity) return false;

        if (AnsweredAsRelay.ContainsKey(identity)) return true;

        if (!AnswersAsRelay(helperPath)) return false;

        AnsweredAsRelay[identity] = true;

        return true;
    }

    /// <summary>The helper file's identity for <see cref="AnsweredAsRelay"/>, or null when there is no file.</summary>
    private static (string, DateTime, long)? IdentityOf(string helperPath)
    {
        var file = new FileInfo(helperPath);

        return file.Exists ? (helperPath, file.LastWriteTimeUtc, file.Length) : null;
    }

    /// <summary>Ask the helper to relay on a loopback port held here for the length of the question, and whether it answered with <see cref="ListenFailedExitCode"/>.</summary>
    private static bool AnswersAsRelay(string helperPath)
    {
        using var held = new TcpListener(IPAddress.Loopback, 0);
        held.Start();

        var (command, args) = Wrap(helperPath, ((IPEndPoint)held.LocalEndpoint).Port, UnusedSocketPath, UnusedCli, []);

        return ExitCodeOf(command, args) == ListenFailedExitCode;
    }

    /// <summary>The exit status of <paramref name="command"/>, or null when it could not start or did not exit within <see cref="AnswerTimeout"/>, which kills it. It reads an empty stdin, and its output is drained and dropped.</summary>
    private static int? ExitCodeOf(string command, IReadOnlyList<string> args)
    {
        var start = new ProcessStartInfo(command) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };

        foreach (var arg in args) start.ArgumentList.Add(arg);
        foreach (var name in McpProxyConnectVariables) start.Environment.Remove(name);

        try
        {
            using var process = Process.Start(start)!;

            process.StandardInput.Close();
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();

            if (process.WaitForExit(AnswerTimeout)) return process.ExitCode;

            process.Kill(entireProcessTree: true);

            return null;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException) { return null; }
    }
}
