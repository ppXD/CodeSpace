using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using CodeSpace.Mcp;
using CodeSpace.Mcp.Relay;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the <c>codespace-mcp relay</c> verb (<see cref="BrokerRelay"/>): the port is listening, on loopback alone,
/// before the CLI starts, and a port it cannot bind never starts the CLI; the CLI is found on PATH, never in the working
/// directory, and gets the environment the relay was given; each TCP connection is spliced to a fresh connection on the
/// broker socket with each direction half-closing on its own, and a socket that cannot be reached resets the client;
/// a signal sent to the relay reaches the CLI; the relay exits with the CLI's status (128+signal for a signal) the
/// moment the CLI does, and never touches stdin or stdout. Tier 🟢: the REAL published helper next to this
/// assembly, spawned as a process over a real loopback port and a real <c>AF_UNIX</c> socket. The sandboxed chain
/// is <c>BrokerRelayE2ETests</c>. POSIX only.
/// </summary>
[Trait("Category", "Unit")]
public sealed class BrokerRelayTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    private readonly List<string> _dirs = [];

    [Fact]
    public void The_verb_and_its_exit_codes_are_pinned()
    {
        // The runner names the verb in every relayed launch's argv, and reads these codes back off the chain's exit.
        BrokerRelay.Verb.ShouldBe("relay");
        BrokerRelay.UsageExitCode.ShouldBe(2, "the proxy's own usage code");
        BrokerRelay.ListenFailedExitCode.ShouldBe(125);
        BrokerRelay.CliNotStartedExitCode.ShouldBe(127);
    }

    [Fact]
    public void Parse_reads_the_port_the_socket_and_the_cli_with_its_own_arguments()
    {
        var command = BrokerRelayCommand.Parse(["43123", "/run/broker/s", "--", "claude", "-p", "--", "x"]);

        command.Port.ShouldBe(43123);
        command.Broker.ToString().ShouldBe("/run/broker/s");
        command.Cli.ShouldBe("claude");
        command.CliArgs.ShouldBe(["-p", "--", "x"], customMessage: "everything after the first separator belongs to the CLI, a second -- included");
    }

    [Theory]
    [InlineData("")]                            // nothing after the verb
    [InlineData("{port}|{sock}|--")]            // no CLI
    [InlineData("{port}|{sock}|-|{cli}")]       // no separator
    [InlineData("abc|{sock}|--|{cli}")]         // not a number
    [InlineData("0|{sock}|--|{cli}")]           // no port to listen on
    [InlineData("65536|{sock}|--|{cli}")]       // past the TCP range
    [InlineData("+80|{sock}|--|{cli}")]         // a sign is not a port
    [InlineData("{port}||--|{cli}")]            // empty socket
    [InlineData("{port}|{longsock}|--|{cli}")]  // longer than a Unix socket address
    [InlineData("{port}|{sock}|--|")]           // empty CLI
    public async Task A_malformed_argv_exits_2_and_starts_nothing(string argv)
    {
        if (OperatingSystem.IsWindows()) return;

        var marker = Path.Combine(TempDir(), "cli-ran");

        var run = await RunRelayAsync(ExpandArgv(argv, marker));

        run.ExitCode.ShouldBe(BrokerRelay.UsageExitCode, $"a malformed argv is a usage error; stderr: {run.Stderr}");
        run.StderrLines.ShouldHaveSingleItem().ShouldContain(BrokerRelayCommand.Usage, customMessage: "one stderr line, naming the usage");
        await ShouldNeverAppearAsync(marker, "the CLI must not start on a usage error");
    }

    [Fact]
    public async Task A_port_already_in_use_exits_125_and_never_starts_the_cli()
    {
        if (OperatingSystem.IsWindows()) return;

        using var occupant = new TcpListener(IPAddress.Loopback, 0);
        occupant.Start();
        var port = ((IPEndPoint)occupant.LocalEndpoint).Port;
        var marker = Path.Combine(TempDir(), "cli-ran");

        var run = await RunRelayAsync([port.ToString(), AbsentSocket, "--", .. MarkerCli(marker)]);

        run.ExitCode.ShouldBe(BrokerRelay.ListenFailedExitCode, $"a port the relay cannot bind fails the launch; stderr: {run.Stderr}");
        run.StderrLines.ShouldHaveSingleItem().ShouldContain($"127.0.0.1:{port}", customMessage: "one stderr line, naming the address it could not bind");

        // The bind comes first: a CLI started before it would run here, against a port nobody relays.
        await ShouldNeverAppearAsync(marker, "the CLI must never start when its broker port cannot be bound");
    }

    [Fact]
    public void Listen_returns_a_socket_already_accepting_on_loopback_only()
    {
        if (OperatingSystem.IsWindows()) return;

        var port = FreePort();

        using var listener = BrokerRelay.Listen(port);

        // The taken-port test pins that Listen runs before the CLI starts; this pins that listen(2) is inside it, so a
        // CLI's first connect can never find the port bound but not yet accepting — which no timing test can see.
        ((int)listener.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.AcceptConnection)!).ShouldBe(1, "the port must already be accepting when Listen returns, before the CLI starts");
        listener.LocalEndPoint.ShouldBe(new IPEndPoint(IPAddress.Loopback, port), "the lease port belongs on loopback; a wildcard bind would expose it on an allowlist namespace's veth");
    }

    [Fact]
    public async Task A_connection_to_another_loopback_address_is_refused()
    {
        // Linux routes all of 127/8 to lo, so a wildcard bind would answer 127.0.0.2; macOS configures only 127.0.0.1,
        // where 127.0.0.2 times out instead of refusing.
        if (!OperatingSystem.IsLinux()) return;

        await using var relay = await HeldRelay.StartAsync(TempDir(), AbsentSocket, exitCode: 0);

        using (await ConnectAsync(relay.Port)) { /* control: the port is listening on 127.0.0.1 */ }

        using var other = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var refused = await Should.ThrowAsync<SocketException>(() => other.ConnectAsync(new IPEndPoint(IPAddress.Parse("127.0.0.2"), relay.Port)).WaitAsync(Deadline));

        refused.SocketErrorCode.ShouldBe(SocketError.ConnectionRefused, "the relay binds 127.0.0.1 alone, never the wildcard");
        (await relay.ReleaseAsync()).ExitCode.ShouldBe(0);
    }

    [Fact]
    public async Task The_cli_reaches_the_port_on_its_very_first_call()
    {
        if (OperatingSystem.IsWindows()) return;

        // bash's /dev/tcp opens the connection without starting another program, so this is as early as a CLI can call.
        // Repeated because a relay that started the CLI before binding would pass whenever it won the race.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var port = FreePort();

            var run = await RunRelayAsync([port.ToString(), AbsentSocket, "--", "/bin/bash", "-c", "if { : <>\"/dev/tcp/127.0.0.1/$0\"; } 2>/dev/null; then echo connected; else echo refused; fi", port.ToString()]);

            run.ExitCode.ShouldBe(0, $"attempt {attempt}: stderr: {run.Stderr}");
            run.Stdout.ShouldBe("connected\n", $"attempt {attempt}: the port must already be listening when the CLI starts — 'refused' means the CLI ran before the bind");
        }
    }

    [Fact]
    public async Task A_connection_is_spliced_to_the_socket_and_each_direction_half_closes_on_its_own()
    {
        if (OperatingSystem.IsWindows()) return;

        // The broker reads the whole request to its EOF and only then replies — so the reply arriving proves the
        // client's half-close was passed on AND did not cut the other direction (the MCP proxy's pump would).
        var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var broker = new SocketStub(TempDir(), async (conn, _) =>
        {
            var request = await ReadToEndAsync(conn);
            seen.TrySetResult(request);

            await conn.SendAsync(Encoding.ASCII.GetBytes("reply-after-eof"), SocketFlags.None);
            conn.Shutdown(SocketShutdown.Send);
        });

        await using var relay = await HeldRelay.StartAsync(TempDir(), broker.Path, exitCode: 0);

        using var client = await ConnectAsync(relay.Port);
        await client.SendAsync(Encoding.ASCII.GetBytes("request-then-eof"), SocketFlags.None);
        client.Shutdown(SocketShutdown.Send);

        (await seen.Task.WaitAsync(Deadline)).ShouldBe("request-then-eof", "the broker must receive the request bytes and then the client's EOF");
        (await ReadToEndAsync(client).WaitAsync(Deadline)).ShouldBe("reply-after-eof", "the reply must flow back after the request direction closed, and end with the broker's EOF");

        (await relay.ReleaseAsync()).ExitCode.ShouldBe(0);
    }

    [Fact]
    public async Task A_connection_the_broker_socket_cannot_take_is_reset()
    {
        if (OperatingSystem.IsWindows()) return;

        await using var relay = await HeldRelay.StartAsync(TempDir(), Path.Combine(TempDir(), "absent"), exitCode: 0);

        using var client = await ConnectAsync(relay.Port);

        var failure = await Should.ThrowAsync<SocketException>(() => client.ReceiveAsync(new byte[16].AsMemory(), SocketFlags.None).AsTask().WaitAsync(Deadline));

        failure.SocketErrorCode.ShouldBe(SocketError.ConnectionReset, "a broker nobody serves must reset the call so the CLI retries it — a clean EOF reads as an empty reply");
        (await relay.ReleaseAsync()).ExitCode.ShouldBe(0, "a failed call does not end the relay");
    }

    [Theory]
    [InlineData("exit 0", 0)]
    [InlineData("exit 7", 7)]
    [InlineData("kill -TERM $$", 143)]   // 128 + SIGTERM, as a shell reports a signalled child
    public async Task The_relay_exits_with_the_cli_s_status_and_writes_nothing_of_its_own(string script, int expected)
    {
        if (OperatingSystem.IsWindows()) return;

        var run = await RunRelayAsync([FreePort().ToString(), AbsentSocket, "--", "/bin/sh", "-c", script]);

        run.ExitCode.ShouldBe(expected, $"the chain must report what the CLI did; stderr: {run.Stderr}");
        run.Stdout.ShouldBeEmpty("stdout is the CLI's parsed stream; the relay must never write to it");
        run.Stderr.ShouldBeEmpty("a relay that started and ran cleanly has nothing to say");
    }

    [Fact]
    public async Task The_relay_exits_the_moment_the_cli_does_even_with_a_call_in_flight()
    {
        if (OperatingSystem.IsWindows()) return;

        // A broker that accepts and never answers: the relay must not wait on it once the CLI has gone.
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var broker = new SocketStub(TempDir(), async (_, stop) =>
        {
            accepted.TrySetResult();
            await Task.Delay(Timeout.Infinite, stop);
        });

        await using var relay = await HeldRelay.StartAsync(TempDir(), broker.Path, exitCode: 3);

        using var client = await ConnectAsync(relay.Port);
        await accepted.Task.WaitAsync(Deadline);

        (await relay.ReleaseAsync()).ExitCode.ShouldBe(3, "the relay must exit with the CLI's status without draining the open call");
    }

    [Theory]
    [InlineData("TERM")]
    [InlineData("INT")]
    [InlineData("HUP")]
    public async Task A_signal_sent_to_the_relay_reaches_the_cli_and_the_relay_exits_with_what_the_cli_did(string signal)
    {
        if (OperatingSystem.IsWindows()) return;

        // Before the relay, the CLI was the process a signal reached. The relay must stay transparent: pass the signal
        // on and wait, not die at once and leave the CLI running on without its broker. The CLI gives up after ~10 s,
        // so one orphaned by a relay that does not forward cannot outlive the test.
        var ready = Path.Combine(TempDir(), "cli.ready");
        var cli = "trap 'echo \"cli-got-$1\" >&2; exit 42' \"$1\"; touch \"$0\"; i=0; while [ $i -lt 200 ]; do sleep 0.05; i=$((i+1)); done; echo cli-never-signalled >&2; exit 9";

        using var relay = Process.Start(RelayStart([FreePort().ToString(), AbsentSocket, "--", "/bin/sh", "-c", cli, ready, signal]))!;
        var stderr = relay.StandardError.ReadToEndAsync();
        await WaitForFileAsync(ready, relay);

        await SignalAsync(relay, signal);
        await WaitOrKillAsync(relay, "relay");

        var said = await stderr.WaitAsync(Deadline);
        relay.ExitCode.ShouldBe(42, $"the relay must forward SIG{signal} to the CLI and exit with the CLI's own status; stderr: {said}");
        said.ShouldBe($"cli-got-{signal}\n", "the CLI's trap ran, and the relay said nothing of its own");
    }

    [Fact]
    public async Task The_relay_already_catches_every_forwarded_signal_when_its_cli_starts()
    {
        if (!OperatingSystem.IsLinux()) return;   // SigCgt is read from /proc

        // The CLI's first act is to read its parent's caught-signal mask, with shell builtins alone so no exec delays it,
        // and so it sees the relay as it was the instant the CLI existed. Registered after the start, the handlers are not
        // there yet (the runtime catches INT and TERM on its own; HUP is the one that tells), and a signal sent the moment
        // the CLI runs meets the relay's default action — the relay dies and leaves its CLI running without its broker.
        // Registered before the start, the mask is complete before the fork, so this can never fail for the right order.
        var run = await RunRelayAsync([FreePort().ToString(), AbsentSocket, "--", "/bin/sh", "-c", "while IFS=: read -r key value; do [ \"$key\" = SigCgt ] && echo $value; done < /proc/$PPID/status; true"]);

        run.ExitCode.ShouldBe(0, $"stderr: {run.Stderr}");
        var caught = Convert.ToUInt64(run.Stdout.Trim(), 16);

        foreach (var (name, number) in new[] { ("HUP", 1), ("INT", 2), ("TERM", 15) })
            ((caught >> (number - 1)) & 1).ShouldBe(1UL, $"SIG{name} must already be caught by the relay when its CLI starts; SigCgt={run.Stdout.Trim()}");
    }

    [Theory]
    [InlineData(null, "unset")]
    [InlineData("3", "3")]
    public async Task The_cli_gets_the_environment_the_relay_was_given_not_the_relay_s_own_thread_setting(string? inherited, string expected)
    {
        if (OperatingSystem.IsWindows()) return;

        var run = await RunRelayAsync([FreePort().ToString(), AbsentSocket, "--", "/bin/sh", "-c", $"echo \"${{{BrokerRelay.SocketEngineThreadsEnvVar}-unset}}\""], start =>
        {
            if (inherited is null) start.Environment.Remove(BrokerRelay.SocketEngineThreadsEnvVar);
            else start.Environment[BrokerRelay.SocketEngineThreadsEnvVar] = inherited;
        });

        run.ExitCode.ShouldBe(0, $"stderr: {run.Stderr}");
        run.Stdout.ShouldBe(expected + "\n", "the relay caps its own socket engine; the CLI must see only what the relay itself was given");
    }

    [Fact]
    public void The_socket_engine_setting_is_pinned()
    {
        // The .NET runtime reads this name, and only from the environment; a typo would silently leave one socket
        // engine thread per 8 (arm64) or 30 (x64) of the host's CPUs in every relay.
        BrokerRelay.SocketEngineThreadsEnvVar.ShouldBe("DOTNET_SYSTEM_NET_SOCKETS_THREAD_COUNT");
    }

    [Fact]
    public async Task Stdin_reaches_the_cli_untouched()
    {
        if (OperatingSystem.IsWindows()) return;

        var prompt = new byte[400_000];
        Random.Shared.NextBytes(prompt);

        var run = await RunRelayAsync([FreePort().ToString(), AbsentSocket, "--", "/bin/sh", "-c", "wc -c"], stdin: prompt);

        run.ExitCode.ShouldBe(0, $"stderr: {run.Stderr}");
        run.Stdout.Trim().ShouldBe("400000", "every stdin byte belongs to the CLI; the relay must read none of them, and write nothing beside the CLI's count");
    }

    [Fact]
    public async Task The_cli_is_resolved_from_PATH_not_from_the_working_directory()
    {
        if (OperatingSystem.IsWindows()) return;

        // The working directory is the repository the run works on. .NET's own bare-name lookup would pick its
        // ./claude before PATH's, and run it holding the brokered bearer.
        var workspace = TempDir();
        var bin = TempDir();
        WriteScript(Path.Combine(workspace, "claude"), "echo REPO-PLANTED");
        WriteScript(Path.Combine(bin, "claude"), "echo PATH");

        var run = await RunRelayAsync([FreePort().ToString(), AbsentSocket, "--", "claude"], start =>
        {
            start.WorkingDirectory = workspace;
            start.Environment["PATH"] = $"{bin}:/usr/bin:/bin";
        });

        run.ExitCode.ShouldBe(0, $"stderr: {run.Stderr}");
        run.Stdout.ShouldBe("PATH\n", "the CLI must come from PATH; a planted file in the working directory must never run");
    }

    [Fact]
    public async Task The_proxy_argv_still_reaches_the_proxy()
    {
        if (OperatingSystem.IsWindows()) return;

        var run = await RunRelayAsync([], start =>
        {
            start.ArgumentList.Clear();
            start.ArgumentList.Add("--proxy");
            start.Environment.Remove(McpProxyEnv.SocketEnvVar);
            start.Environment.Remove(McpProxyEnv.TokenEnvVar);
        });

        run.ExitCode.ShouldBe(2, "the proxy's usage code");
        run.Stderr.ShouldContain(McpProxyEnv.SocketEnvVar, customMessage: "the --proxy argv must reach the proxy's own env resolution, not the relay's usage");
    }

    public void Dispose()
    {
        foreach (var dir in _dirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>A broker socket nobody serves. Most tests never call through the relay, so where it points does not matter.</summary>
    private const string AbsentSocket = "/tmp/cs-relay-absent/s";

    private static string RelayBinary => Path.Combine(AppContext.BaseDirectory, "codespace-mcp");

    /// <summary>A CLI whose only act is to leave <paramref name="marker"/> behind — how a test sees that it started.</summary>
    private static string[] MarkerCli(string marker) => ["/bin/sh", "-c", "touch \"$0\"", marker];

    /// <summary>A '|'-separated argv template with <c>{port}</c>, <c>{sock}</c>, <c>{longsock}</c> and <c>{cli}</c> (the marker CLI) filled in.</summary>
    private static string[] ExpandArgv(string template, string marker)
    {
        if (template.Length == 0) return [];

        var withCli = template.Replace("{cli}", string.Join('|', MarkerCli(marker)));
        var withPort = withCli.Replace("{port}", FreePort().ToString());

        return withPort.Replace("{sock}", AbsentSocket).Replace("{longsock}", "/tmp/" + new string('s', 200)).Split('|');
    }

    private string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cs-relay-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);

        return dir;
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();

        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    [UnsupportedOSPlatform("windows")]
    private static void WriteScript(string path, string body)
    {
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>Waits half a second (a hundred times what the marker CLI takes) for the file a wrongly started CLI would write at once, then asserts it never came.</summary>
    private static async Task ShouldNeverAppearAsync(string path, string because)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        File.Exists(path).ShouldBeFalse(because);
    }

    private static ProcessStartInfo RelayStart(IReadOnlyList<string> relayArgs)
    {
        var start = new ProcessStartInfo(RelayBinary) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(BrokerRelay.Verb);
        foreach (var arg in relayArgs) start.ArgumentList.Add(arg);

        return start;
    }

    private static async Task<RelayRun> RunRelayAsync(IReadOnlyList<string> relayArgs, Action<ProcessStartInfo>? configure = null, byte[]? stdin = null)
    {
        var start = RelayStart(relayArgs);
        configure?.Invoke(start);

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        await FeedAsync(process, stdin);
        await WaitOrKillAsync(process, "relay");

        return new RelayRun(process.ExitCode, await stdout, await stderr);
    }

    private static async Task FeedAsync(Process process, byte[]? stdin)
    {
        try
        {
            if (stdin is not null) await process.StandardInput.BaseStream.WriteAsync(stdin);
            process.StandardInput.Close();
        }
        catch (IOException) { /* the CLI exited without reading it all — its own count says so */ }
    }

    private static async Task WaitOrKillAsync(Process process, string what)
    {
        try { await process.WaitForExitAsync().WaitAsync(Deadline); }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"the {what} did not exit within {Deadline.TotalSeconds}s — check `ps -ef | grep codespace-mcp` for a relay still waiting on its CLI or a connection");
        }
    }

    /// <summary>Waits for the file a relay's CLI touches once it runs — so a test knows the CLI started, and so the port is open.</summary>
    private static async Task WaitForFileAsync(string path, Process relay)
    {
        var watch = Stopwatch.StartNew();

        while (!File.Exists(path))
        {
            watch.Elapsed.ShouldBeLessThan(Deadline, $"the relay's CLI never started; relay exited={relay.HasExited}");
            await Task.Delay(20);
        }
    }

    private static async Task SignalAsync(Process process, string signal)
    {
        using var kill = Process.Start("/bin/sh", ["-c", "kill -s \"$0\" \"$1\"", signal, process.Id.ToString()]);
        await kill.WaitForExitAsync().WaitAsync(Deadline);

        kill.ExitCode.ShouldBe(0, $"kill -s {signal} {process.Id} failed — the relay exited before it was signalled?");
    }

    private static async Task<Socket> ConnectAsync(int port)
    {
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port)).WaitAsync(Deadline);

        return client;
    }

    private static async Task<string> ReadToEndAsync(Socket socket)
    {
        var buffer = new byte[4096];
        var all = new MemoryStream();

        int read;
        while ((read = await socket.ReceiveAsync(buffer.AsMemory(), SocketFlags.None)) > 0) all.Write(buffer, 0, read);

        return Encoding.ASCII.GetString(all.ToArray());
    }

    private sealed record RelayRun(int ExitCode, string Stdout, string Stderr)
    {
        public string[] StderrLines => Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>A relay whose CLI signals it started and then waits to be released with a chosen exit code — so a test can make calls through a live relay. The CLI starts only after the bind, so once it has signalled, the port is open.</summary>
    private sealed class HeldRelay : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly string _release;

        public int Port { get; }

        private HeldRelay(Process process, string release, int port)
        {
            _process = process;
            _release = release;
            Port = port;
        }

        public static async Task<HeldRelay> StartAsync(string dir, string socketPath, int exitCode)
        {
            var port = FreePort();
            var release = Path.Combine(dir, "release");
            var start = new ProcessStartInfo(RelayBinary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };

            foreach (var arg in new[] { BrokerRelay.Verb, port.ToString(), socketPath, "--", "/bin/sh", "-c", "touch \"$0.ready\"; while [ ! -e \"$0\" ]; do sleep 0.05; done; exit \"$1\"", release, exitCode.ToString() })
                start.ArgumentList.Add(arg);

            var relay = new HeldRelay(Process.Start(start)!, release, port);
            await WaitForFileAsync(release + ".ready", relay._process);

            return relay;
        }

        public async Task<Process> ReleaseAsync()
        {
            await File.WriteAllTextAsync(_release, "");
            await WaitOrKillAsync(_process, "relay");

            return _process;
        }

        public ValueTask DisposeAsync()
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            _process.Dispose();

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>The broker end: a Unix socket in <c>dir</c> that hands every accepted connection to <c>serve</c>, stopped on dispose.</summary>
    private sealed class SocketStub : IDisposable
    {
        private readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        private readonly CancellationTokenSource _stop = new();

        public string Path { get; }

        public SocketStub(string dir, Func<Socket, CancellationToken, Task> serve)
        {
            Path = System.IO.Path.Combine(dir, "s");
            _listener.Bind(new UnixDomainSocketEndPoint(Path));
            _listener.Listen(8);

            _ = Task.Run(async () =>
            {
                while (true)
                {
                    Socket conn;
                    try { conn = await _listener.AcceptAsync(); } catch { return; }

                    _ = Task.Run(async () => { using (conn) { try { await serve(conn, _stop.Token); } catch { /* the test asserts what reached it */ } } });
                }
            });
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Dispose();
        }
    }
}
