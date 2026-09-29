using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace CodeSpace.Mcp.Relay;

/// <summary>
/// The <c>codespace-mcp relay &lt;port&gt; &lt;socket&gt; -- &lt;cli&gt; [args…]</c> verb: the sandbox-side end of a run's
/// model broker. A child whose network namespace is not the worker's cannot reach the broker's loopback port, so the
/// worker also serves the lease on a per-run Unix socket bound into the sandbox. This relay runs inside, listens on
/// <c>127.0.0.1:&lt;port&gt;</c> (the address already in the CLI's base URL) and splices every accepted connection to a
/// fresh connection on that socket. It parses nothing, so a streamed (SSE) reply reaches the CLI as it arrives.
///
/// <para>It is the CLI's parent, not a sidecar, because two orderings matter. The port is listening BEFORE the CLI
/// starts, so the CLI's first call can never find it closed; a port that cannot be bound fails the launch with
/// <see cref="ListenFailedExitCode"/> and the CLI never runs. And the relay exits with the CLI's own status (128+signal
/// for a signalled CLI) the moment the CLI exits, without draining open connections, so the chain reports what the
/// agent did and the stdout FIFO's EOF never waits on the relay. It never reads stdin and never writes stdout: both
/// belong to the CLI, which inherits them. A SIGHUP, SIGINT or SIGTERM sent to the relay is passed on to the CLI, which
/// was the process it reached before the relay; the relay waits and exits with whatever the CLI then does.</para>
///
/// <para>Every relay's threads count against the one task cap the worker's uid shares with all its runs, and inside the
/// sandbox it sees the host's CPU count, not the run's cgroup's. So it runs one socket engine thread and at most
/// <see cref="PoolThreads"/> pool workers, whatever that count is, and its CLI inherits none of that.</para>
///
/// <para>The CLI is started through <c>/bin/sh -c 'exec "$0" "$@"'</c>, never by .NET's bare-name resolution. That
/// looks in the executable's directory and the working directory before PATH, and the working directory is the
/// repository the run works on, so a planted <c>./claude</c> would run holding the brokered bearer.</para>
///
/// <para>Not a trust boundary: the agent can reach the same socket itself. The broker's bearer and route check remain
/// the capability.</para>
/// </summary>
internal static class BrokerRelay
{
    /// <summary>The argv[0] that selects this verb. Any other argv is the MCP proxy, unchanged.</summary>
    internal const string Verb = "relay";

    /// <summary>A malformed argv. The proxy's own usage code.</summary>
    internal const int UsageExitCode = 2;

    /// <summary>The port could not be bound, so the CLI was never started.</summary>
    internal const int ListenFailedExitCode = 125;

    /// <summary>The shell that runs the CLI could not be started. The same code the shell itself gives a CLI missing from PATH.</summary>
    internal const int CliNotStartedExitCode = 127;

    /// <summary>The socket engine thread count. The .NET runtime reads it from the environment only, when the process makes its first socket.</summary>
    internal const string SocketEngineThreadsEnvVar = "DOTNET_SYSTEM_NET_SOCKETS_THREAD_COUNT";

    /// <summary>The most pool workers the relay runs. Nothing it does blocks one, so four carry any CLI's calls.</summary>
    private const int PoolThreads = 4;

    private const string Shell = "/bin/sh";

    private const string ExecThroughPath = "exec \"$0\" \"$@\"";

    private const int ListenBacklog = 64;

    private const int BufferSize = 16 * 1024;

    /// <summary>The signals passed on to the CLI, with the numbers Linux and macOS both give them.</summary>
    private static readonly (PosixSignal Signal, int Number)[] ForwardedSignals = [(PosixSignal.SIGHUP, 1), (PosixSignal.SIGINT, 2), (PosixSignal.SIGTERM, 15)];

    internal static int Run(string[] args)
    {
        try
        {
            var command = BrokerRelayCommand.Parse(args);
            var inheritedSocketEngineThreads = CapThreads();   // before Listen: the first socket fixes the engine count

            using var listener = Listen(command.Port);

            // Before the CLI exists: a signal that met the relay's default action would kill it and leave the CLI
            // running without its broker. One that lands before the CLI has a pid is held and delivered once it does.
            var signals = new SignalRelay(Kill);
            using var forwarding = ForwardSignals(signals);
            using var cli = StartCli(command, inheritedSocketEngineThreads);

            signals.Started(cli.Id);

            _ = AcceptLoopAsync(listener, command.Broker);

            return ExitCodeOf(cli);
        }
        catch (ArgumentException ex) { return Fail(ex.Message, UsageExitCode); }
        catch (ListenFailedException ex) { return Fail(ex.Message, ListenFailedExitCode); }
        catch (CliNotStartedException ex) { return Fail(ex.Message, CliNotStartedExitCode); }
    }

    // ── Launch ──────────────────────────────────────────────────────────────

    /// <summary>Bind and listen on <c>127.0.0.1:<paramref name="port"/></c> alone. The socket is already accepting when this returns, before the CLI starts.</summary>
    internal static Socket Listen(int port)
    {
        var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        try
        {
            listener.Bind(new IPEndPoint(IPAddress.Loopback, port));
            listener.Listen(ListenBacklog);

            return listener;
        }
        catch (SocketException ex)
        {
            listener.Dispose();
            throw new ListenFailedException($"cannot listen on 127.0.0.1:{port} ({ex.SocketErrorCode}: {ex.Message}); the CLI was not started.");
        }
    }

    /// <summary>Start the CLI with stdio inherited, resolved by the shell's PATH lookup, in the environment the relay was given. The listener is close-on-exec, as every .NET socket is, so the CLI never holds the port.</summary>
    private static Process StartCli(BrokerRelayCommand command, string? inheritedSocketEngineThreads)
    {
        var start = new ProcessStartInfo(Shell) { UseShellExecute = false };

        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(ExecThroughPath);
        start.ArgumentList.Add(command.Cli);

        foreach (var arg in command.CliArgs) start.ArgumentList.Add(arg);

        RestoreSocketEngineThreads(start.Environment, inheritedSocketEngineThreads);

        try { return Process.Start(start) ?? throw new CliNotStartedException($"cannot start {Shell} to run '{command.Cli}'."); }
        catch (Win32Exception ex) { throw new CliNotStartedException($"cannot start {Shell} to run '{command.Cli}' ({ex.Message})."); }
    }

    private static int ExitCodeOf(Process cli)
    {
        cli.WaitForExit();

        return cli.ExitCode;
    }

    private static int Fail(string message, int exitCode)
    {
        Console.Error.WriteLine($"codespace-mcp relay: {message}");

        return exitCode;
    }

    // ── Threads ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Hold the relay to a fixed handful of threads, whatever CPU count it sees: uncapped, .NET starts a socket engine
    /// per 8 (arm64) or 30 (x64) CPUs and adds pool workers per busy stream. Returns the engine setting the relay was
    /// given, so the CLI gets that back instead of the relay's own.
    /// </summary>
    private static string? CapThreads()
    {
        var inherited = Environment.GetEnvironmentVariable(SocketEngineThreadsEnvVar);

        Environment.SetEnvironmentVariable(SocketEngineThreadsEnvVar, "1");

        // The floor (one per CPU by default) has to drop before the ceiling can go below it.
        ThreadPool.SetMinThreads(1, 1);
        ThreadPool.SetMaxThreads(PoolThreads, PoolThreads);

        return inherited;
    }

    private static void RestoreSocketEngineThreads(IDictionary<string, string?> environment, string? inherited)
    {
        if (inherited is null) environment.Remove(SocketEngineThreadsEnvVar);
        else environment[SocketEngineThreadsEnvVar] = inherited;
    }

    // ── Signals ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Pass a terminating signal sent to the relay on to the CLI instead of dying of it. Before the relay, the CLI was
    /// the process that signal reached; a relay that died would leave the CLI running on without its broker, and the
    /// chain would report a status the CLI never had.
    /// </summary>
    private static SignalForwarding ForwardSignals(SignalRelay signals)
    {
        var registrations = ForwardedSignals.Select(forwarded => PosixSignalRegistration.Create(forwarded.Signal, context => Forward(context, signals, forwarded.Number)));

        return new SignalForwarding(registrations.ToArray());
    }

    private static void Forward(PosixSignalContext context, SignalRelay signals, int signal)
    {
        context.Cancel = true;   // the relay stays, and exits with whatever the CLI does about the signal

        signals.Received(signal);
    }

    /// <summary>Send <paramref name="signal"/> to <paramref name="pid"/>. ESRCH once the CLI has gone: the relay is then already on its way out with its status.</summary>
    private static void Kill(int pid, int signal) => kill(pid, signal);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    /// <summary>
    /// Where a signal the relay receives goes: straight to the CLI once it has a pid, else held and delivered the moment
    /// it has one — the relay's handlers are in place before the CLI starts, so a signal can land first. Of several held
    /// signals the CLI gets the last; each of them asks it to stop. The hold is re-checked after it is made, so a start
    /// that ran in between still delivers it, and an atomic take makes sure nothing is delivered twice.
    /// </summary>
    internal sealed class SignalRelay(Action<int, int> deliver)
    {
        private int _cliPid;
        private int _held;

        /// <summary>Run between reading no pid and holding the signal — the interleaving only the re-check saves. Null in production.</summary>
        internal Action? BeforeHoldForTest { get; init; }

        /// <summary>Run between holding the signal and the re-check. Null in production.</summary>
        internal Action? AfterHoldForTest { get; init; }

        /// <summary>A signal reached the relay: pass it on, or hold it until the CLI has a pid.</summary>
        public void Received(int signal)
        {
            if (Volatile.Read(ref _cliPid) is var pid and not 0)
            {
                deliver(pid, signal);
                return;
            }

            BeforeHoldForTest?.Invoke();

            // An Exchange, not a Volatile.Write: it is also the full fence the re-check below relies on, so either the
            // start takes the held signal or the re-check sees its pid. A plain write lets both miss it.
            Interlocked.Exchange(ref _held, signal);

            AfterHoldForTest?.Invoke();

            if (Volatile.Read(ref _cliPid) is var started and not 0) DeliverHeld(started);
        }

        /// <summary>The CLI has a pid: deliver whatever arrived before it did.</summary>
        public void Started(int cliPid)
        {
            Volatile.Write(ref _cliPid, cliPid);

            DeliverHeld(cliPid);
        }

        private void DeliverHeld(int pid)
        {
            if (Interlocked.Exchange(ref _held, 0) is var held and not 0) deliver(pid, held);
        }
    }

    private sealed class SignalForwarding(PosixSignalRegistration[] registrations) : IDisposable
    {
        public void Dispose()
        {
            foreach (var registration in registrations) registration.Dispose();
        }
    }

    // ── Relay ───────────────────────────────────────────────────────────────

    private static async Task AcceptLoopAsync(Socket listener, UnixDomainSocketEndPoint broker)
    {
        while (true)
        {
            Socket client;

            try { client = await listener.AcceptAsync().ConfigureAwait(false); }
            catch (SocketException) { continue; }        // one connection aborted before it was accepted
            catch (ObjectDisposedException) { return; }  // the CLI exited and the relay is going with it

            _ = RelayConnectionAsync(client, broker);
        }
    }

    /// <summary>One connection: a fresh socket connection per TCP connection, spliced both ways. A broker that cannot be reached resets the client (linger 0) so the CLI sees a failed call it retries, not a hang or a clean empty reply.</summary>
    private static async Task RelayConnectionAsync(Socket client, UnixDomainSocketEndPoint broker)
    {
        using var accepted = client;
        using var upstream = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        try
        {
            // A streamed reply is many small writes; Nagle would hold each behind the CLI's delayed ACK of the last.
            accepted.NoDelay = true;

            await upstream.ConnectAsync(broker).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            Reset(accepted);
            return;
        }

        await Task.WhenAll(CopyAsync(accepted, upstream), CopyAsync(upstream, accepted)).ConfigureAwait(false);
    }

    /// <summary>Copy one direction until its EOF, then half-close the other side's send so the peer sees the same EOF while the opposite direction keeps flowing. A failure either way aborts both, so neither copy waits on a dead peer.</summary>
    private static async Task CopyAsync(Socket from, Socket to)
    {
        var buffer = new byte[BufferSize];

        try
        {
            int read;

            while ((read = await from.ReceiveAsync(buffer.AsMemory(), SocketFlags.None).ConfigureAwait(false)) > 0)
                await SendAllAsync(to, buffer.AsMemory(0, read)).ConfigureAwait(false);

            to.Shutdown(SocketShutdown.Send);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            Reset(from);
            Reset(to);
        }
    }

    private static async Task SendAllAsync(Socket to, ReadOnlyMemory<byte> data)
    {
        while (!data.IsEmpty)
            data = data[await to.SendAsync(data, SocketFlags.None).ConfigureAwait(false)..];
    }

    private static void Reset(Socket socket)
    {
        try { socket.LingerState = new LingerOption(true, 0); }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { /* already closed, or a Unix socket that has no RST to send */ }

        socket.Dispose();
    }

    // ── Exit reasons ────────────────────────────────────────────────────────

    private sealed class ListenFailedException(string message) : Exception(message);

    private sealed class CliNotStartedException(string message) : Exception(message);
}
