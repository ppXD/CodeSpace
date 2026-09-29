using System.Diagnostics;
using System.Security.Cryptography;
using CodeSpace.Core.Services.Agents.Credentials.Broker;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using Shouldly;
using Xunit.Abstractions;

namespace CodeSpace.SandboxTests;

/// <summary>
/// 🟢 Sandbox isolation E2E (high fidelity, Rule 12): the REAL model-credential broker reached by a REAL process in a
/// network namespace of its own, over a live kernel.
///
/// <para><b>The claim it settles.</b> A namespaced run cannot reach the broker's loopback port, so "the broker is
/// reachable from inside" cannot be argued from the code, it has to be observed: through the per-run socket, bound
/// read-only into the sandbox (<see cref="A_severed_child_reaches_its_broker_through_the_lease_socket_and_the_same_child_reaches_the_next_worker"/>,
/// which needs only bubblewrap and so runs as root and as the unprivileged worker uid alike), through the relay the
/// production chain puts in front of the CLI, for a network-off and an allowlist child, until the lease is revoked —
/// and, for a run launched before the relay whose child calls its namespace's gateway, that a re-bind leaves nothing
/// answering there (<see cref="A_gateway_addressed_child_reaches_nothing_after_a_socketless_rebind"/>).</para>
///
/// <para>The second claim is the flip side: the bearer the sandbox holds is NOT the tenant's key. Sent straight to the
/// provider it buys nothing, so a token that escapes a run is not a credential.</para>
///
/// <para>Every arm that ran prints <see cref="RanMarker"/> with the uid it ran as, which the lanes require.</para>
/// </summary>
[Trait("Category", "Sandbox")]
public sealed class ModelCredentialBrokerNetnsE2ETests(ITestOutputHelper output)
{
    /// <summary>Printed by the socket-channel arm when it actually ran, with the uid it ran as — the evidence a lane needs that it did not return early.</summary>
    public const string RanMarker = "[broker-socket-e2e] ran";

    private const string ProviderHost = "api.anthropic.com";

    /// <summary>
    /// 🟢 High fidelity (Rule 12): the REAL broker serves a lease over its per-run socket, and a REAL bubblewrap child —
    /// the production <see cref="BubblewrapSandbox.BuildArgs"/> with <c>--unshare-net</c>, the socket's directory bound
    /// READ-ONLY — reaches it by speaking HTTP straight over <c>AF_UNIX</c>. The relay that will carry a CLI's TCP onto
    /// that socket is not part of the broker, so python3 plays its part; everything on the worker's side is production,
    /// and the socket path comes from the production layout.
    ///
    /// <para><b>Why the restart is the arm that matters.</b> The child is started ONCE and kept. Worker A goes away and
    /// takes its socket file with it; worker B re-binds the run's recorded address and re-opens the socket at the same
    /// path. The child's bind pins the socket DIRECTORY's inode, so B's socket is visible to it only because the directory
    /// was never deleted — delete and recreate it and this same child can never reach a model again, while the re-bind
    /// reports the run restored.</para>
    /// </summary>
    [Fact]
    public Task A_severed_child_reaches_its_broker_through_the_lease_socket_and_the_same_child_reaches_the_next_worker() => SocketChannelAsync(lane: "root");

    /// <summary>The socket-channel arm, for either lane: see the test above.</summary>
    internal async Task SocketChannelAsync(string lane)
    {
        if (BubblewrapSandbox.Available is not { } bwrap)
        {
            BubblewrapSandbox.IsRequired.ShouldBeFalse("Sandbox:RequireConfinement is set but this host cannot sandbox (bwrap/userns) — the E2E cannot prove the broker's socket channel here");
            return;
        }

        using var context = new SocketChannelContext();
        var runId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var workerA = LoopbackModelCredentialBroker.ForTest(new AlwaysOkUpstream());

        try
        {
            var brokered = (await workerA.OpenAsync(LeaseFor(runId, teamId) with { SocketPath = context.SocketPath }, CancellationToken.None)).ShouldNotBeNull("the broker must be able to open a lease on a host that confines");

            brokered.SocketPath.ShouldBe(context.SocketPath, $"the lease must bind its socket at the path it was given; check `ls -la {context.SocketDirectory}`");
            workerA.ListenerPrefixForTest(runId).ShouldBe($"http://127.0.0.1:{brokered.RebindPort}/", "a lease served over a socket binds its TCP listener on loopback only, even on a host that could build namespaces — its namespaced children come in through the socket");

            using var child = SeveredChild.Start(bwrap, context, ReachableUrl(brokered), brokered.RunToken);

            (await child.AskAsync("call")).ShouldBe("200", $"a severed child must reach its broker through the socket its sandbox binds read-only; diagnose by hand with `curl --unix-socket {context.SocketPath} -X POST -H 'Authorization: Bearer <token>' http://127.0.0.1/<route>/v1/messages`. Child stderr: {child.Stderr}");
            (await child.AskAsync("tcp")).ShouldBe("refused", "the child's network is its own: the broker's TCP port on the host's loopback does not exist in it, so the socket is its one door");
            (await child.AskAsync("unlink")).ShouldBe("EROFS", "the socket's directory is bound READ-ONLY: the child can connect through it but can neither delete the worker's socket nor plant one of its own");

            workerA.Dispose();

            File.Exists(context.SocketPath).ShouldBeFalse("worker A's close must take its socket file with it");
            Directory.Exists(context.SocketDirectory).ShouldBeTrue("…and must leave the directory, which the child's bind pins");
            (await child.AskAsync("call")).ShouldStartWith("error:", customMessage: "with worker A gone the child's call must fail — that is the restart this arm is about");

            var upstreamB = new AlwaysOkUpstream();
            using var workerB = LoopbackModelCredentialBroker.ForTest(upstreamB, logger: new TestOutputLogger<LoopbackModelCredentialBroker>(output));

            (await workerB.RebindAsync(RebindOf(brokered, runId, teamId, epoch: 2) with { SocketPath = context.SocketPath }, CancellationToken.None)).ShouldBeTrue("worker B must re-open the run's recorded address, socket and all");

            (await child.AskAsync("call")).ShouldBe("200",
                $"the SAME child must reach worker B through the socket re-opened at the same path. If it cannot, the socket's directory was replaced rather than kept: the child's bind still points at the old inode. Compare `stat -c %i {context.SocketDirectory}` on the host with the directory the child sees. Child stderr: {child.Stderr}");
            upstreamB.Calls.ShouldBe(1, "and it was worker B that relayed it");

            await workerB.RevokeAsync(runId, "e2e-revoke", fencedToEpoch: null, CancellationToken.None);

            (await child.AskAsync("call")).ShouldStartWith("error:", customMessage: "a revoked lease's socket must answer nothing");
            upstreamB.Calls.ShouldBe(1, "and nothing more reached the provider");

            output.WriteLine($"{RanMarker} {(lane == "root" ? "" : lane + " ")}socket-channel uid={EffectiveUid()}");
        }
        finally { workerA.Dispose(); }
    }

    /// <summary>The effective uid this test process runs as, read where the kernel states it — so the marker says which lane actually ran it.</summary>
    private static string EffectiveUid() =>
        File.ReadLines("/proc/self/status").First(line => line.StartsWith("Uid:", StringComparison.Ordinal)).Split('\t', StringSplitOptions.RemoveEmptyEntries)[2];

    /// <summary>
    /// One run's socket, placed by the PRODUCTION layout (<see cref="LocalProcessRunner.ModelBrokerSocketPathFor"/>) under
    /// a GUID-keyed run, plus a scratch working directory for the child. Removes the socket's own directory and the
    /// run's spool directory — never the layout root, which other runs share (Rule 12.2/12.3).
    /// </summary>
    private sealed class SocketChannelContext : IDisposable
    {
        private readonly string _spoolKey = Guid.NewGuid().ToString("N");

        public SocketChannelContext()
        {
            SocketPath = LocalProcessRunner.ModelBrokerSocketPathFor(_spoolKey, CodeSpace.Core.Services.Agents.Mcp.McpRunToken.MintPathId());
            Directory.CreateDirectory(WorkingDirectory);
        }

        public string SocketPath { get; }

        public string SocketDirectory => Path.GetDirectoryName(SocketPath)!;

        public string WorkingDirectory { get; } = Path.Combine(Path.GetTempPath(), "cs-broker-e2e-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            foreach (var directory in new[] { SocketDirectory, WorkingDirectory, LocalProcessRunner.SpoolDirectoryFor(_spoolKey) })
                try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// A child confined exactly as the default tier confines one — the production bubblewrap argv, network severed —
    /// kept alive across the whole test and driven one command per line over stdin, so every answer comes from the SAME
    /// process whose bind was taken before the restart.
    /// </summary>
    private sealed class SeveredChild : IDisposable
    {
        private readonly Process _process;
        private readonly System.Text.StringBuilder _stderr = new();

        private SeveredChild(Process process)
        {
            _process = process;
            _process.ErrorDataReceived += (_, line) => { lock (_stderr) _stderr.AppendLine(line.Data); };
            _process.BeginErrorReadLine();
        }

        public string Stderr { get { lock (_stderr) return _stderr.ToString(); } }

        public static SeveredChild Start(string bwrap, SocketChannelContext context, string brokerUrl, string runToken)
        {
            var plan = new BwrapPlan
            {
                Command = "/usr/bin/python3", Args = ["-u", "-c", ChildScript], ShareNetwork = false,
                ReadOnlyExtraPaths = [context.SocketDirectory], WorkingDirectory = context.WorkingDirectory, WritablePaths = [context.WorkingDirectory],
            };
            var psi = new ProcessStartInfo(bwrap) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };

            foreach (var argument in BubblewrapSandbox.BuildArgs(plan)) psi.ArgumentList.Add(argument);

            psi.Environment["BROKER_SOCKET"] = context.SocketPath;
            psi.Environment["BROKER_URL"] = brokerUrl;
            psi.Environment["RUN_TOKEN"] = runToken;

            return new SeveredChild(Process.Start(psi)!);
        }

        /// <summary>Send one command and wait for its one-line answer — bounded, and naming the command, so a child that hangs fails this test with something to go on (Rule 12.10).</summary>
        public async Task<string> AskAsync(string command)
        {
            await _process.StandardInput.WriteLineAsync(command);
            await _process.StandardInput.FlushAsync();

            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            try { return await _process.StandardOutput.ReadLineAsync(budget.Token) ?? throw new Xunit.Sdk.XunitException($"the child exited before answering '{command}' (exit {(_process.HasExited ? _process.ExitCode : -1)}); its stderr: {Stderr}"); }
            catch (OperationCanceledException) { throw new Xunit.Sdk.XunitException($"the child did not answer '{command}' within 30s; its stderr: {Stderr}"); }
        }

        public void Dispose()
        {
            try { _process.StandardInput.Close(); } catch { /* already gone */ }
            try { if (!_process.WaitForExit(5000)) _process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            _process.Dispose();
        }

        /// <summary>
        /// The child: one command per line. <c>call</c> POSTs a model call over the socket and prints the status (or
        /// <c>error:&lt;type&gt;</c>); <c>tcp</c> tries the broker's TCP port on loopback; <c>unlink</c> tries to delete the
        /// worker's socket. The request is written by hand, byte for byte what a relay would forward, addressed to
        /// <c>127.0.0.1</c> as a relayed CLI addresses it.
        /// </summary>
        private const string ChildScript = """
            import errno, os, socket, sys, urllib.parse
            url = urllib.parse.urlparse(os.environ['BROKER_URL'])
            path, token = os.environ['BROKER_SOCKET'], os.environ['RUN_TOKEN']
            def call():
                try:
                    s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM); s.settimeout(15); s.connect(path)
                    s.sendall(('POST %s/v1/messages HTTP/1.1\r\nHost: %s\r\nAuthorization: Bearer %s\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}' % (url.path, url.netloc, token)).encode())
                    data = b''
                    while True:
                        chunk = s.recv(65536)
                        if not chunk: break
                        data += chunk
                    s.close()
                    return data.split(b' ')[1].decode() if data.startswith(b'HTTP/') else 'error:no-response'
                except Exception as e:
                    return 'error:' + type(e).__name__
            def tcp():
                try:
                    socket.create_connection(('127.0.0.1', url.port), timeout=5).close(); return 'open'
                except ConnectionRefusedError: return 'refused'
                except Exception as e: return 'error:' + type(e).__name__
            def unlink():
                try:
                    os.unlink(path); return 'unlinked'
                except OSError as e: return errno.errorcode.get(e.errno, str(e.errno))
            for line in sys.stdin:
                print({'call': call, 'tcp': tcp, 'unlink': unlink}[line.strip()](), flush=True)
            """;
    }

    [Theory]
    [InlineData(false)]   // a network-off run: bubblewrap gives it a fresh namespace with only loopback
    [InlineData(true)]    // an allowlist run: the filtered namespace, which bubblewrap shares
    public async Task A_namespaced_run_reaches_its_broker_and_is_refused_the_moment_the_lease_is_revoked(bool allowlist)
    {
        if (BubblewrapSandbox.Available is not { } bwrap || allowlist && !FilteredEgressNetns.IsSupported) return;   // the sandbox lane, with bwrap (and ip + nft for the allowlist arm), is authoritative

        var upstream = new AlwaysOkUpstream();
        using var broker = LoopbackModelCredentialBroker.ForTest(upstream);
        using var context = new SocketChannelContext();
        var runId = Guid.NewGuid();
        var brokered = (await broker.OpenAsync(LeaseFor(runId, Guid.NewGuid()) with { SocketPath = context.SocketPath }, CancellationToken.None)).ShouldNotBeNull("the broker must be able to lease on a host that confines");
        var netnsKey = Guid.NewGuid().ToString("N");
        var prefix = Array.Empty<string>() as IReadOnlyList<string>;

        try
        {
            if (allowlist)
            {
                var setup = await FilteredEgressNetns.SetupAsync(netnsKey, Array.Empty<string>(), timeoutSeconds: 20, CancellationToken.None);
                setup.SetupOk.ShouldBeTrue($"the allowlist netns must set up cleanly; setup error: {setup.SetupError}");
                prefix = setup.ExecPrefix;
            }

            // The PRODUCTION chain around the child — the runner's own composition of namespace, bubblewrap and relay —
            // with curl as the CLI, so each call is one relayed child from start to exit.
            var spec = new SandboxSpec { Command = "/usr/bin/curl", AllowNetwork = allowlist, EgressAllowlist = allowlist ? ["api.anthropic.com"] : null, ModelBrokerPort = brokered.RebindPort, ModelBrokerSocketPath = brokered.SocketPath, WorkingDirectory = context.WorkingDirectory };
            var url = ReachableUrl(brokered) + "/v1/messages";

            (await RelayedCurlAsync(spec, prefix, bwrap, url, brokered.RunToken)).ShouldBe((0, "200"),
                customMessage: $"a {(allowlist ? "allowlist" : "network-off")} run must reach its broker at {url} through the relay and its socket; check `ls -la {context.SocketDirectory}`");

            var relayedBeforeRevoke = upstream.Calls;

            await broker.RevokeAsync(runId, "e2e-revoke", fencedToEpoch: null, CancellationToken.None);

            // A revoke withdraws the ADDRESS, not just the routing entry: the lease's socket and its listener both go.
            // What the relayed child observes is a connection the relay resets, never an HTTP answer.
            var (exit, status) = await RelayedCurlAsync(spec, prefix, bwrap, url, brokered.RunToken);

            exit.ShouldNotBe(0, $"after a revoke nothing may answer the relayed child — curl got status '{status}'");
            upstream.Calls.ShouldBe(relayedBeforeRevoke,
                "and nothing may reach the provider after the withdrawal — that, not which error the sandbox sees, is what decides whether a cancelled run can still spend the tenant's key");

            output.WriteLine($"{RanMarker} revoke-{(allowlist ? "allowlist" : "network-off")} uid={EffectiveUid()} exitAfterRevoke={exit}");
        }
        finally { if (allowlist) await FilteredEgressNetns.TeardownAsync(netnsKey, CancellationToken.None); }
    }

    /// <summary>
    /// 🟢 High fidelity (Rule 12): the gateway path to the broker is gone, observed on a live kernel. The run it served
    /// was launched before the relay: its child froze its broker's address at its namespace's GATEWAY, and its handle
    /// recorded a port, a route and a bearer but no socket. It is staged as it was, on a 10.x lease as the old allocator
    /// handed them out, in the allowlist plan's namespace as it was built before its veth was guarded
    /// (<see cref="PreGuardPlan"/>), so the only thing that decides whether the gateway answers is the broker's bind.
    ///
    /// <para>A socketless re-bind on the next worker binds loopback like every lease: the worker reaches it there, and
    /// the child at its gateway is refused. The control then binds the SAME port at the gateway and is answered, so the
    /// refusal is the broker's bind and not a namespace that could not reach its gateway at all.</para>
    /// </summary>
    [Fact]
    public async Task A_gateway_addressed_child_reaches_nothing_after_a_socketless_rebind()
    {
        if (!FilteredEgressNetns.IsSupported) return;   // the root lane, with ip + nft, is authoritative

        FilteredEgressNetns.CanFilter.ShouldBeTrue($"ip and nft are here, but this process could not build an allowlist namespace ({FilteredEgressNetns.FilterUnavailableReason}) — the survivor this arm stands for could not be staged");

        var runId = Guid.NewGuid();
        var netnsKey = Guid.NewGuid().ToString("N");
        var lease = LegacyLease();

        try
        {
            var setup = await FilteredEgressNetns.ApplyAsync(netnsKey, PreGuardPlan(netnsKey, lease), timeoutSeconds: 20, CancellationToken.None);
            setup.SetupOk.ShouldBeTrue($"the pre-guard allowlist netns must set up on {lease.Cidr}; setup error: {setup.SetupError}");

            // What the survivor's handle recorded: a port, a route and a bearer, and no socket.
            var port = FreeLoopbackPort();
            var route = CodeSpace.Core.Services.Agents.Mcp.McpRunToken.MintPathId();
            var token = CodeSpace.Core.Services.Agents.Mcp.McpRunToken.Mint();
            var gatewayUrl = $"http://{lease.HostIp}:{port}/{route}/v1/messages";

            using var workerB = LoopbackModelCredentialBroker.ForTest(new AlwaysOkUpstream());

            (await workerB.RebindAsync(SocketlessRebind(runId, port, route, token), CancellationToken.None)).ShouldBeTrue("the next worker must re-open the recorded port");
            workerB.ListenerPrefixForTest(runId).ShouldBe($"http://127.0.0.1:{port}/", "a re-bind without a socket binds loopback like every lease, on a host that builds namespaces too");

            (await CurlAsync($"http://127.0.0.1:{port}/{route}/v1/messages", token)).ShouldBe((0, "200"), customMessage: "control: the re-bound lease answers where it binds, so the refusal below is about where it binds");

            var (gatewayExit, gatewayStatus) = await CurlAsync(gatewayUrl, token, setup.ExecPrefix);

            gatewayExit.ShouldBe(7,
                customMessage: $"a child calling its gateway must be refused (curl exit 7) — got exit {gatewayExit}, status '{gatewayStatus}'. Exit 0 means the broker answered at the gateway, so a wide bind is back; check by hand: `ip netns exec {FilteredEgressPlan.NamespaceFor(netnsKey)} curl -v {gatewayUrl}`");

            (await AnsweredAtGatewayAsync(lease.HostIp, port, setup.ExecPrefix)).ShouldBe("204",
                customMessage: $"control: a listener bound at the gateway on the same port must be answered from inside, or the refusal above proved nothing about the broker's bind — check `ip netns exec {FilteredEgressPlan.NamespaceFor(netnsKey)} ip route`");

            output.WriteLine($"{RanMarker} gateway-retired uid={EffectiveUid()} lease={lease.Cidr} gatewayExit={gatewayExit}");
        }
        finally { await FilteredEgressNetns.TeardownAsync(netnsKey, CancellationToken.None); }
    }

    /// <summary>A /30 from the range the allocator walked (10.1.1.0 up to 10.254.254.252) before it moved to 198.19.64.0–198.19.191.255, which is where a run launched before the relay still sits. Its top /16, random within it, so repeated runs rarely share one.</summary>
    private static EgressSubnetAllocator.Lease LegacyLease()
    {
        var second = RandomNumberGenerator.GetInt32(1, 255);
        var third = RandomNumberGenerator.GetInt32(0, 64) * 4;

        return new() { Cidr = $"10.254.{second}.{third}/30", HostIp = $"10.254.{second}.{third + 1}", NsIp = $"10.254.{second}.{third + 2}" };
    }

    /// <summary>
    /// The allowlist plan a run launched before the guard on its veth was built with: the same setup and forward table
    /// (<see cref="FilteredEgressPlan.BuildNftRuleset"/>), and no inet table of the plan's own. A new namespace drops what
    /// its child sends the worker, gateway included; a survivor's does not, which is how it reached its broker there.
    /// </summary>
    private static FilteredEgressPlan PreGuardPlan(string netnsKey, EgressSubnetAllocator.Lease lease)
    {
        var plan = FilteredEgressPlan.Build(netnsKey, Array.Empty<string>(), lease, Array.Empty<string>());

        return plan with { NftRuleset = FilteredEgressPlan.BuildNftRuleset(plan.Namespace, lease.Cidr, Array.Empty<string>(), Array.Empty<string>()) };
    }

    /// <summary>The re-bind a survivor's handle yields: its recorded port, route and bearer, and no socket path.</summary>
    private static ModelCredentialRebindRequest SocketlessRebind(Guid runId, int port, string route, string token) => new()
    {
        RunId = runId, TeamId = Guid.NewGuid(), Epoch = 2, Port = port, PathId = route, RunToken = token,
        Upstream = new() { Provider = "Anthropic", ApiKey = "sk-e2e-upstream-key" }, Ttl = TimeSpan.FromMinutes(5),
    };

    /// <summary>Bind <paramref name="port"/> at the gateway <paramref name="hostIp"/>, answer one request 204, and return the status the namespace's curl saw — or curl's exit code when it saw none.</summary>
    private static async Task<string> AnsweredAtGatewayAsync(string hostIp, int port, IReadOnlyList<string> execPrefix)
    {
        using var control = new System.Net.HttpListener();
        control.Prefixes.Add($"http://{hostIp}:{port}/");
        control.Start();

        _ = AnswerOnceAsync(control);

        var (exit, status) = await CurlAsync($"http://{hostIp}:{port}/", "control", execPrefix);

        return exit == 0 ? status : $"curl exit {exit}";
    }

    private static async Task AnswerOnceAsync(System.Net.HttpListener listener)
    {
        var context = await listener.GetContextAsync();

        context.Response.StatusCode = 204;
        context.Response.Close();
    }

    /// <summary>A port free on loopback right now, allocated by the kernel (Rule 12.8).</summary>
    private static int FreeLoopbackPort()
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();

        return ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
    }

    /// <summary>
    /// One relayed child under the PRODUCTION chain (<see cref="LocalProcessRunner.ChildCommand"/>): the namespace prefix
    /// if any, bubblewrap, the relay, and curl POSTing to <paramref name="url"/>. Returns curl's exit code and the HTTP
    /// status it saw.
    ///
    /// <para>Forked from a thread of its own that lives until the chain is done: <c>--die-with-parent</c> is
    /// <c>PR_SET_PDEATHSIG</c>, which fires when the forking THREAD exits, and a pool thread can retire mid-call — a
    /// SIGKILL'd chain, exit 137, whatever the broker answered. The production runner launches from a thread that
    /// outlives the command for the same reason, and so does <c>BrokerRelayE2ETests</c>.</para>
    /// </summary>
    private static Task<(int Exit, string Status)> RelayedCurlAsync(SandboxSpec spec, IReadOnlyList<string> prefix, string bwrap, string url, string token)
    {
        var withArgs = spec with { Args = ["-s", "-m", "15", "-o", "/dev/null", "-w", "%{http_code}", "-X", "POST", "-H", "content-type: application/json", "-H", $"Authorization: Bearer {token}", "-d", "{}", url] };
        var argv = LocalProcessRunner.ChildCommand(new LocalProcessRunner.CommandIsolationContext(withArgs, null, null, prefix, Array.Empty<string>()), bwrap, prlimit: null);

        argv.ShouldContain(ModelBrokerRelay.Verb, "fixture check: the production chain put the relay in front of curl");

        var done = new TaskCompletionSource<(int Exit, string Status)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var launcher = new Thread(() =>
        {
            try { done.SetResult(RunChainToExit(argv)); }
            catch (Exception exception) { done.SetException(exception); }
        }) { IsBackground = true, Name = "relayed-curl-launcher" };

        launcher.Start();

        return done.Task;
    }

    /// <summary>Start <paramref name="argv"/> on the calling thread and wait for it there, bounded (Rule 12.10): curl's own <c>-m 15</c> ends any call well inside the deadline, so a chain still running past it is the relay waiting on something after its CLI.</summary>
    private static (int Exit, string Status) RunChainToExit(IReadOnlyList<string> argv)
    {
        var psi = new ProcessStartInfo { FileName = argv[0], UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in argv.Skip(1)) psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!Task.WhenAll(process.WaitForExitAsync(), stdout, stderr).Wait(TimeSpan.FromSeconds(60)))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already exited */ }
            throw new TimeoutException("the relayed curl chain did not finish within 60s although curl gives up at 15s — check `ps -ef | grep -e bwrap -e codespace-mcp` for a relay left waiting");
        }

        return (process.ExitCode, stdout.Result.Trim());
    }

    private static ModelCredentialLeaseRequest LeaseFor(Guid runId, Guid teamId) =>
        new() { RunId = runId, TeamId = teamId, Epoch = 1, Upstream = new() { Provider = "Anthropic", ApiKey = "sk-e2e-upstream-key" }, Ttl = TimeSpan.FromMinutes(5) };

    /// <summary>The re-bind the next worker builds from what the run's durable handle carries — every value restored from <paramref name="brokered"/>, because the child's configuration froze all of them at launch.</summary>
    private static ModelCredentialRebindRequest RebindOf(BrokeredModelCredential brokered, Guid runId, Guid teamId, long epoch) => new()
    {
        RunId = runId, TeamId = teamId, Epoch = epoch, Port = brokered.RebindPort!.Value, PathId = brokered.RebindRoute!,
        RunToken = brokered.RunToken, Upstream = new() { Provider = "Anthropic", ApiKey = "sk-e2e-upstream-key" }, Ttl = TimeSpan.FromMinutes(5),
    };

    [Fact]
    public async Task A_run_token_presented_to_the_provider_directly_is_refused()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var broker = LoopbackModelCredentialBroker.ForTest(new AlwaysOkUpstream());

        var brokered = await broker.OpenAsync(
            new() { RunId = Guid.NewGuid(), TeamId = Guid.NewGuid(), Epoch = 1, Upstream = new() { Provider = "Anthropic", ApiKey = "sk-e2e-upstream-key" }, Ttl = TimeSpan.FromMinutes(5) },
            CancellationToken.None);

        if (brokered is null) return;

        var (exit, status) = await CurlAsync($"https://{ProviderHost}/v1/messages", brokered.RunToken);

        if (exit != 0) return;   // no egress from this runner at all — nothing to observe, and the CI job with network is authoritative

        status.ShouldNotBe("200",
            customMessage: $"the per-run bearer must be worthless at {ProviderHost} — if it were accepted there, it would BE a provider credential and a token that escaped the sandbox would be a leak of one");
        int.Parse(status).ShouldBeGreaterThanOrEqualTo(400,
            customMessage: $"the provider must REFUSE the run token outright (got {status})");
    }

    private static string ReachableUrl(BrokeredModelCredential brokered)
    {
        var spec = new SandboxSpec { Command = "curl", Environment = new Dictionary<string, string> { ["URL"] = brokered.BaseUrl } };

        return LocalProcessRunner.ResolveModelBrokerHost(spec).Environment["URL"];
    }

    /// <summary>POST to <paramref name="url"/> with the bearer and report (curl exit code, HTTP status) — from this worker, or from inside a namespace behind <paramref name="execPrefix"/> when one is given.</summary>
    private static async Task<(int Exit, string Status)> CurlAsync(string url, string token, IReadOnlyList<string>? execPrefix = null)
    {
        var argv = (execPrefix ?? Array.Empty<string>()).Concat(new[]
        {
            "curl", "-s", "-m", "15", "-o", "/dev/null", "-w", "%{http_code}",
            "-X", "POST", "-H", "content-type: application/json", "-H", $"Authorization: Bearer {token}",
            "-d", "{}", url,
        }).ToList();

        var psi = new ProcessStartInfo { FileName = argv[0], UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in argv.Skip(1)) psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, stdout.Trim());
    }

    /// <summary>The provider, answering 200 to anything the broker relays — this lane asserts REACHABILITY and REFUSAL, never what a model said.</summary>
    private sealed class AlwaysOkUpstream : HttpMessageHandler
    {
        private int _calls;

        /// <summary>How many requests the broker has RELAYED. The count is what "withdrawn" actually means — the sandbox's own error is a symptom, this is the fact that decides whether a cancelled run can still spend the tenant's key. Interlocked because the relay serves each request on its own task.</summary>
        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") });
        }
    }
}
