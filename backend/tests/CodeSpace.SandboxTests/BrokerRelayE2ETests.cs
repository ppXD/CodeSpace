using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using Shouldly;
using Xunit.Abstractions;

namespace CodeSpace.SandboxTests;

/// <summary>
/// 🟢 Sandbox isolation E2E (high fidelity, Rule 12): the REAL published <c>codespace-mcp relay</c> as the command of
/// the PRODUCTION bubblewrap argv (<see cref="BubblewrapSandbox.BuildArgs"/> with the network severed, so a fresh
/// namespace with only loopback), its broker a Unix socket bound in read-only from the host, and a CLI under it that
/// streams from that broker. What only a real kernel can show: the CLI reaches the host's socket through a namespace
/// with no route anywhere; an SSE reply reaches it event by event (the broker withholds every stream's second event
/// until the CLI has acknowledged the first over another connection); 400 KB of stdin reaches the CLI byte for byte
/// while stdout carries only what the CLI wrote; the CLI's exit code and its signal come back out of the chain;
/// neither the relay, the CLI nor the sandbox around them outlives the chain; and, with eight streams flowing at once
/// on a production-sized CPU count, the relay stays under a pinned thread and memory ceiling, since every relay counts
/// against the one task budget the worker's uid shares with all its runs.
///
/// <para>Needs bwrap and python3, so it runs for real only in the privileged sandbox-isolation job; elsewhere it
/// returns. Every arm that ran prints <see cref="RanMarker"/>, which the lane requires, so a silent return can never
/// pass for coverage.</para>
/// </summary>
[Trait("Category", "Sandbox")]
public sealed class BrokerRelayE2ETests(ITestOutputHelper output) : IDisposable
{
    /// <summary>Printed by every arm that actually ran; the sandbox lane requires one per arm in the test output.</summary>
    public const string RanMarker = "[broker-relay-e2e] ran";

    /// <summary>
    /// The relay's thread ceiling. At most 14 by construction, measured at 13-14 on 5 and 96 CPUs with 2 to 64 streams:
    /// main, finalizer, signal handler, one socket engine, PAL sync manager, tiered compilation, three diagnostics-server
    /// threads, the pool gate and at most four pool workers; two to spare for a background GC or a signal handler.
    /// Uncapped, .NET starts a socket engine per 8 (arm64) or 30 (x64) CPUs and pool workers per busy stream, and the
    /// relay reads the host's CPU count, not the run's cgroup: 26 to 29 threads on 96 CPUs. Past this is a density
    /// regression: the worker, every run and every relay share one task cap under one uid.
    /// </summary>
    private const int RelayThreadCeiling = 16;

    /// <summary>The relay's pool-worker cap (<c>BrokerRelay.PoolThreads</c>); the socket engine is capped at one thread.</summary>
    private const int RelayPoolWorkerCeiling = 4;

    /// <summary>The relay's resident-memory ceiling, measured at about 36 MB. Native AOT is the way down if it ever matters.</summary>
    private const int RelayRssCeilingKb = 64 * 1024;

    /// <summary>The CPU count the relay is made to see: a large worker node's, where an uncapped relay grows most.</summary>
    private const int ProductionSizedHostCpus = 96;

    /// <summary>Streams the CLI holds open through the relay at once, each carrying <see cref="AfterAckEvents"/> events after the acknowledgement.</summary>
    private const int ConcurrentStreams = 8;

    private const int AfterAckEvents = 50;

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    private readonly List<string> _dirs = [];

    [Theory]
    [InlineData("7", 7)]
    [InlineData("sigterm", 143)]   // 128 + SIGTERM, what the chain reports for a CLI killed by it
    public async Task A_cli_behind_the_relay_streams_from_the_broker_socket_and_its_stdio_and_status_pass_through(string end, int expectedExit)
    {
        if (!Confines()) return;

        var run = NewRun();
        using var broker = new SseBrokerStub(run.SocketPath);
        var cliToken = "cs-relay-cli-" + Guid.NewGuid().ToString("N");
        var prompt = RandomNumberGenerator.GetBytes(400_000);

        var env = new Dictionary<string, string> { ["CLI_END"] = end, ["RELAY_STREAMS"] = ConcurrentStreams.ToString(), ["DOTNET_PROCESSOR_COUNT"] = ProductionSizedHostCpus.ToString() };

        var chain = await RunChainAsync(run, ["/usr/bin/python3", "-c", StreamingCli, cliToken], env, prompt, seeRelayBeforeStdin: true);

        chain.ExitCode.ShouldBe(expectedExit, $"the chain must report what the CLI did (CLI_END={end}); stderr: {chain.Stderr}");
        var cli = ParseCli(chain);

        Ints(cli, "statuses").ShouldBe(Enumerable.Repeat(200, ConcurrentStreams), $"every stream must reach the host's broker socket from a namespace with no route anywhere; cli: {cli}");
        Strings(cli, "firsts").ShouldBe(Enumerable.Repeat<string?>("data: one\n", ConcurrentStreams), $"each stream's first event arrives on its own; cli: {cli}");
        cli.GetProperty("ack").GetString().ShouldBe("ok", "another connection through the relay is served while the streams are still open");
        Ints(cli, "after_ack_events").ShouldBe(Enumerable.Repeat(AfterAckEvents, ConcurrentStreams), $"the broker sends the rest of a stream only once the CLI has acknowledged every first event, so 0 means the relay held a first event back until more bytes came; cli: {cli}");
        cli.GetProperty("stdin_bytes").GetInt32().ShouldBe(prompt.Length, "every stdin byte is the CLI's; the relay must read none of them");
        cli.GetProperty("stdin_sha256").GetString().ShouldBe(Convert.ToHexStringLower(SHA256.HashData(prompt)), "and they arrive unaltered");
        cli.GetProperty("relay_comm").GetString().ShouldBe("codespace-mcp", "the CLI's parent is the relay itself: the shell it was started through exec'd into it");

        var threads = cli.GetProperty("relay_threads").GetInt32();
        var names = cli.GetProperty("relay_thread_names").GetString()!.Split(',');
        var rssKb = cli.GetProperty("relay_rss_kb").GetInt32();
        threads.ShouldBeLessThanOrEqualTo(RelayThreadCeiling, $"every relay's threads count against the task cap the worker's uid shares with all its runs; threads: {string.Join(',', names)}");
        names.Count(name => name == ".NET Sockets").ShouldBe(1, $"one socket engine thread, however many CPUs the relay sees ({ProductionSizedHostCpus} here); threads: {string.Join(',', names)}");
        names.Count(name => name == ".NET TP Worker").ShouldBeLessThanOrEqualTo(RelayPoolWorkerCeiling, $"the pool is capped, however many streams are busy ({ConcurrentStreams} here); threads: {string.Join(',', names)}");
        rssKb.ShouldBeLessThanOrEqualTo(RelayRssCeilingKb, "one relay runs beside every relayed CLI");

        await ShouldLeaveNothingBehindAsync([cliToken, run.SocketPath]);

        output.WriteLine($"{RanMarker} {(end == "sigterm" ? "sigterm" : "exit-7")} exit={chain.ExitCode} threads={threads} rss_kb={rssKb} names={cli.GetProperty("relay_thread_names").GetString()}");
    }

    [Fact]
    public async Task The_cli_reaches_the_relay_on_its_very_first_call()
    {
        if (!Confines()) return;

        // bash's /dev/tcp connects without starting another program, so this is as early as a CLI can call. Repeated
        // because a relay that started the CLI before binding would pass whenever it won the race.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var run = NewRun();

            var chain = await RunChainAsync(run, ["/bin/bash", "-c", "if { : <>\"/dev/tcp/127.0.0.1/$RELAY_PORT\"; } 2>/dev/null; then echo connected; else echo refused; fi"], new(), stdin: [], seeRelayBeforeStdin: false);

            chain.ExitCode.ShouldBe(0, $"attempt {attempt}: stderr: {chain.Stderr}");
            chain.Stdout.ShouldBe("connected\n", $"attempt {attempt}: the relay's port must already be listening when the CLI starts — 'refused' means the CLI ran before the bind");
        }

        output.WriteLine($"{RanMarker} immediate-call");
    }

    public void Dispose()
    {
        foreach (var dir in _dirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ── The CLI ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A CLI stand-in that does what the real ones do through the relay: reads its whole prompt from stdin, holds
    /// several streamed calls open while it makes another, reads them all at once, and then ends as it is told. While
    /// the streams flow it samples its parent's (the relay's) threads and memory and keeps the peak. Its one JSON line is
    /// all it writes to stdout, so stdout holding anything else is the relay's doing. Its argv carries a unique token so
    /// the host can tell it is gone.
    /// </summary>
    private const string StreamingCli = """
        import hashlib, http.client, json, os, signal, sys, threading, time
        data = sys.stdin.buffer.read()
        res = {'stdin_bytes': len(data), 'stdin_sha256': hashlib.sha256(data).hexdigest()}
        port, n = int(os.environ['RELAY_PORT']), int(os.environ['RELAY_STREAMS'])
        streams = []
        for _ in range(n):
            c = http.client.HTTPConnection('127.0.0.1', port, timeout=30)
            c.request('GET', '/stream')
            streams.append(c.getresponse())
        res['statuses'] = [r.status for r in streams]
        res['firsts'] = [r.readline().decode() for r in streams]
        ack = http.client.HTTPConnection('127.0.0.1', port, timeout=30)
        ack.request('GET', '/ack')
        res['ack'] = ack.getresponse().read().decode()
        rests = [''] * n
        def read(i): rests[i] = streams[i].read().decode()
        readers = [threading.Thread(target=read, args=(i,)) for i in range(n)]
        for t in readers: t.start()
        relay = f'/proc/{os.getppid()}'
        def comm(path):
            try: return open(path).read().strip()
            except OSError: return '?'
        res['relay_comm'] = comm(f'{relay}/comm')
        res['relay_threads'], res['relay_rss_kb'], res['relay_thread_names'] = 0, 0, ''
        while any(t.is_alive() for t in readers):
            status = dict(l.split(':', 1) for l in open(f'{relay}/status').read().splitlines() if ':' in l)
            if int(status['Threads']) > res['relay_threads']:
                res['relay_threads'] = int(status['Threads'])
                res['relay_thread_names'] = ','.join(sorted(comm(f'{relay}/task/{t}/comm') for t in os.listdir(f'{relay}/task')))
            res['relay_rss_kb'] = max(res['relay_rss_kb'], int(status['VmRSS'].split()[0]))
            time.sleep(0.01)
        res['after_ack_events'] = [r.count('data: two after-ack') for r in rests]
        print(json.dumps(res), flush=True)
        if os.environ['CLI_END'] == 'sigterm':
            os.kill(os.getpid(), signal.SIGTERM)
        os._exit(int(os.environ['CLI_END']))
        """;

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>A host with bwrap confines, and there a missing helper or python3 is a failure, not a skip.</summary>
    private static bool Confines()
    {
        if (BubblewrapSandbox.Available is null)
        {
            BubblewrapSandbox.IsRequired.ShouldBeFalse("Sandbox:RequireConfinement is set but this host cannot sandbox (bwrap/userns) — the relay E2E cannot run here");
            return false;
        }

        File.Exists(LocalProcessRunner.McpProxyBinaryPath()).ShouldBeTrue($"the codespace-mcp helper must be at '{LocalProcessRunner.McpProxyBinaryPath()}' — check CopyMcpProxyToOutput in CodeSpace.SandboxTests.csproj");
        File.Exists("/usr/bin/python3").ShouldBeTrue("the relay E2E's CLI stand-in needs /usr/bin/python3, which the sandbox lane installs");
        return true;
    }

    private RelayRun NewRun()
    {
        var root = Path.Combine(Path.GetTempPath(), "cs-relay-e2e-" + Guid.NewGuid().ToString("N")[..12]);
        var socketDir = Path.Combine(root, "broker");
        var workspace = Path.Combine(root, "ws");

        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(socketDir);
        _dirs.Add(root);

        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();

        return new RelayRun(((IPEndPoint)probe.LocalEndpoint).Port, Path.Combine(socketDir, "s"), socketDir, workspace);
    }

    /// <summary>
    /// Run <paramref name="cli"/> under the relay under the production bwrap argv, network severed, with the helper's
    /// directory and the socket's directory bound read-only. With <paramref name="seeRelayBeforeStdin"/>, the relay must
    /// show in this host's process table before stdin is written and closed (a CLI that reads stdin first is still
    /// waiting then) — the control that makes the later "nothing left" scan mean something.
    ///
    /// <para>Forked from a thread of its own that lives until the chain is done: <c>--die-with-parent</c> is
    /// <c>PR_SET_PDEATHSIG</c>, which fires when the forking THREAD exits, and a pool thread can retire mid-run (a
    /// SIGKILL'd chain, exit 137). The production runner launches from a thread that outlives the command for the same
    /// reason.</para>
    /// </summary>
    private static Task<ChainRun> RunChainAsync(RelayRun run, IReadOnlyList<string> cli, Dictionary<string, string> env, byte[] stdin, bool seeRelayBeforeStdin)
    {
        var done = new TaskCompletionSource<ChainRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        var launcher = new Thread(() =>
        {
            try { done.SetResult(RunChain(ChainStart(run, cli, env), $"{LocalProcessRunner.McpProxyBinaryPath()} relay {run.Port} {run.SocketPath} --", stdin, seeRelayBeforeStdin)); }
            catch (Exception ex) { done.SetException(ex); }
        }) { IsBackground = true, Name = "relay-e2e-launcher" };

        launcher.Start();
        return done.Task;
    }

    private static ProcessStartInfo ChainStart(RelayRun run, IReadOnlyList<string> cli, Dictionary<string, string> env)
    {
        var helper = LocalProcessRunner.McpProxyBinaryPath();
        var plan = new BwrapPlan
        {
            Command = helper,
            Args = ["relay", run.Port.ToString(), run.SocketPath, "--", .. cli],
            ShareNetwork = false,
            WorkingDirectory = run.Workspace,
            WritablePaths = [run.Workspace],
            ReadOnlyExtraPaths = [Path.GetDirectoryName(helper)!, run.SocketDir],
        };

        var start = new ProcessStartInfo(BubblewrapSandbox.Available!) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in BubblewrapSandbox.BuildArgs(plan)) start.ArgumentList.Add(arg);
        foreach (var (name, value) in env) start.Environment[name] = value;
        start.Environment["RELAY_PORT"] = run.Port.ToString();

        return start;
    }

    private static ChainRun RunChain(ProcessStartInfo start, string relayArgv, byte[] stdin, bool seeRelayBeforeStdin)
    {
        using var chain = Process.Start(start)!;
        var stdout = chain.StandardOutput.ReadToEndAsync();
        var stderr = chain.StandardError.ReadToEndAsync();

        if (seeRelayBeforeStdin) WaitForRelay(relayArgv, chain);

        chain.StandardInput.BaseStream.Write(stdin);
        chain.StandardInput.Close();

        // The pipes are awaited under the same deadline as the exit: bwrap reports pid 2's status as soon as it exits,
        // but its init stays until every process in the sandbox is gone, holding stdout open with it.
        if (!Task.WhenAll(chain.WaitForExitAsync(), stdout, stderr).Wait(Deadline))
        {
            try { chain.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already exited */ }
            throw new TimeoutException($"the relay chain did not finish within {Deadline.TotalSeconds}s: bwrap {(chain.HasExited ? "exited, but a process inside still holds its stdout open" : "is still running, so the relay may be waiting on a connection after its CLI exited")} — check `ps -ef | grep -e bwrap -e codespace-mcp`");
        }

        return new ChainRun(chain.ExitCode, stdout.Result, stderr.Result);
    }

    private static int[] Ints(JsonElement cli, string name) => cli.GetProperty(name).EnumerateArray().Select(item => item.GetInt32()).ToArray();

    private static string?[] Strings(JsonElement cli, string name) => cli.GetProperty(name).EnumerateArray().Select(item => item.GetString()).ToArray();

    private static JsonElement ParseCli(ChainRun chain)
    {
        var lines = chain.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        lines.Length.ShouldBe(1, $"stdout must hold only the CLI's one JSON line — anything else is the relay writing to the CLI's parsed stream; stdout: {chain.Stdout}; stderr: {chain.Stderr}");
        return JsonDocument.Parse(lines[0]).RootElement.Clone();
    }

    /// <summary>Waits until a process whose argv STARTS with <paramref name="relayArgv"/> is in this host's process table: the relay itself, not bwrap, whose argv carries the same words further on.</summary>
    private static void WaitForRelay(string relayArgv, Process chain)
    {
        var watch = Stopwatch.StartNew();

        while (!Cmdlines().Any(cmdline => cmdline.StartsWith(relayArgv, StringComparison.Ordinal)))
        {
            chain.HasExited.ShouldBeFalse($"the chain exited before its relay was ever seen (exit {(chain.HasExited ? chain.ExitCode : 0)})");
            watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(15), $"control: the relay ('{relayArgv}') never appeared in /proc, so a scan for leftovers would prove nothing");
            Thread.Sleep(20);
        }
    }

    private static async Task ShouldLeaveNothingBehindAsync(IReadOnlyList<string> tokens)
    {
        var watch = Stopwatch.StartNew();
        IReadOnlyList<string> left;

        while ((left = Cmdlines().Where(cmdline => tokens.Any(token => cmdline.Contains(token, StringComparison.Ordinal))).ToList()).Count > 0 && watch.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(50);

        left.ShouldBeEmpty("neither the relay, its CLI nor the sandbox around them may outlive the chain — check by hand with `ps -ef | grep -e codespace-mcp -e cs-relay`");
    }

    private static IEnumerable<string> Cmdlines() =>
        Directory.EnumerateDirectories("/proc").Where(dir => int.TryParse(Path.GetFileName(dir), out _)).Select(ReadCmdline);

    private static string ReadCmdline(string procDir)
    {
        try { return File.ReadAllText(Path.Combine(procDir, "cmdline")).Replace('\0', ' '); }
        catch (IOException) { return ""; }                 // the process exited while we looked
        catch (UnauthorizedAccessException) { return ""; }
    }

    private sealed record RelayRun(int Port, string SocketPath, string SocketDir, string Workspace);

    private sealed record ChainRun(int ExitCode, string Stdout, string Stderr);

    /// <summary>
    /// The broker end: HTTP/1.1 over the run's Unix socket. <c>/stream</c> answers as SSE, sending its first event and
    /// withholding the rest until <c>/ack</c> has been called, then <see cref="AfterAckEvents"/> more 20 ms apart (or,
    /// after 15 s without it, one event saying it was never acknowledged).
    /// </summary>
    private sealed class SseBrokerStub : IDisposable
    {
        private readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        private readonly TaskCompletionSource _acked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SseBrokerStub(string socketPath)
        {
            _listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            _listener.Listen(ConcurrentStreams * 2);

            _ = AcceptLoopAsync();
        }

        public void Dispose() => _listener.Dispose();

        private async Task AcceptLoopAsync()
        {
            while (true)
            {
                Socket conn;
                try { conn = await _listener.AcceptAsync(); } catch { return; }

                _ = ServeAsync(conn);
            }
        }

        private async Task ServeAsync(Socket conn)
        {
            using var _ = conn;

            try
            {
                var path = (await ReadRequestHeadAsync(conn)).Split(' ')[1];

                if (path == "/ack") await AckAsync(conn);
                else await StreamAsync(conn);
            }
            catch (Exception ex) when (ex is SocketException or IndexOutOfRangeException) { /* the CLI's assertions report what did not arrive */ }
        }

        private async Task AckAsync(Socket conn)
        {
            _acked.TrySetResult();

            await SendAsync(conn, "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
            conn.Shutdown(SocketShutdown.Send);
        }

        private async Task StreamAsync(Socket conn)
        {
            await SendAsync(conn, "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n");
            await SendAsync(conn, Chunk("data: one\n\n"));

            var acked = await Task.WhenAny(_acked.Task, Task.Delay(TimeSpan.FromSeconds(15))) == _acked.Task;

            for (var i = 0; acked && i < AfterAckEvents; i++)
            {
                await SendAsync(conn, Chunk($"data: two after-ack {i}\n\n"));
                await Task.Delay(TimeSpan.FromMilliseconds(20));
            }

            await SendAsync(conn, (acked ? "" : Chunk("data: two unacked\n\n")) + "0\r\n\r\n");
            conn.Shutdown(SocketShutdown.Send);
        }

        private static string Chunk(string data) => $"{Encoding.ASCII.GetByteCount(data):x}\r\n{data}\r\n";

        private static async Task SendAsync(Socket conn, string text) => await conn.SendAsync(Encoding.ASCII.GetBytes(text), SocketFlags.None);

        private static async Task<string> ReadRequestHeadAsync(Socket conn)
        {
            var head = new StringBuilder();
            var buffer = new byte[1024];

            while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await conn.ReceiveAsync(buffer.AsMemory(), SocketFlags.None);
                if (read == 0) break;
                head.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            return head.ToString();
        }
    }
}
