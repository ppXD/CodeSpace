using System.Diagnostics;
using CodeSpace.Core.Services.Agents.Credentials.Broker;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using Shouldly;
using Xunit.Abstractions;

namespace CodeSpace.SandboxTests;

/// <summary>
/// 🟢 Sandbox isolation E2E (high fidelity, Rule 12): the REAL model-credential broker reached by a REAL process
/// inside a REAL deny-by-default network namespace, over a live kernel. Needs ip + nft + CAP_NET_ADMIN, so it runs
/// for real ONLY in the privileged sandbox-isolation CI job; elsewhere
/// <see cref="FilteredEgressNetns.IsSupported"/> is false and it degrade-skips.
///
/// <para><b>The claim it settles.</b> A sealed run's whole point is that its broker is the only way out — so "the
/// broker is reachable from inside" cannot be argued from the code, it has to be observed. The namespace is the
/// production sealed one (<see cref="FilteredEgressNetns.SetupSealedAsync"/>): no route, no NAT, no DNS, and an input
/// filter admitting only the lease's own port on the gateway. The broker still answers, because a packet addressed to
/// the host's own veth address is delivered locally (INPUT) and that one port is what the filter admits. If that ever
/// stops being true, every sealed brokered run loses its model and this test is where it shows.</para>
///
/// <para>The second claim is the flip side: the bearer the sandbox holds is NOT the tenant's key. Sent straight to the
/// provider it buys nothing, so a token that escapes a run is not a credential.</para>
///
/// <para>The third is the per-run socket (<see cref="A_severed_child_reaches_its_broker_through_the_lease_socket_and_the_same_child_reaches_the_next_worker"/>),
/// which needs only bubblewrap — no ip, no nft, no privilege — so it runs as root and as an unprivileged worker uid
/// alike, and prints <see cref="RanMarker"/> with the uid it ran as.</para>
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
    public async Task A_severed_child_reaches_its_broker_through_the_lease_socket_and_the_same_child_reaches_the_next_worker()
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
            brokered.ReachableFromNamespace.ShouldBeFalse("a lease served over a socket binds its TCP listener on loopback only, even on a host that could build namespaces — its namespaced children come in through the socket");

            using var child = SeveredChild.Start(bwrap, context, ReachableUrl(brokered, gatewayIp: null), brokered.RunToken);

            (await child.AskAsync("call")).ShouldBe("200", $"a severed child must reach its broker through the socket its sandbox binds read-only; diagnose by hand with `curl --unix-socket {context.SocketPath} -X POST -H 'Authorization: Bearer <token>' http://127.0.0.1/<route>/v1/messages`. Child stderr: {child.Stderr}");
            (await child.AskAsync("tcp")).ShouldBe("refused", "the child's network is its own: the broker's TCP port on the host's loopback does not exist in it, so the socket is its one door");
            (await child.AskAsync("unlink")).ShouldBe("EROFS", "the socket's directory is bound READ-ONLY: the child can connect through it but can neither delete the worker's socket nor plant one of its own");

            workerA.Dispose();

            File.Exists(context.SocketPath).ShouldBeFalse("worker A's close must take its socket file with it");
            Directory.Exists(context.SocketDirectory).ShouldBeTrue("…and must leave the directory, which the child's bind pins");
            (await child.AskAsync("call")).ShouldStartWith("error:", customMessage: "with worker A gone the child's call must fail — that is the restart this arm is about");

            var upstreamB = new AlwaysOkUpstream();
            using var workerB = LoopbackModelCredentialBroker.ForTest(upstreamB);

            (await workerB.RebindAsync(RebindOf(brokered, runId, teamId, epoch: 2) with { SocketPath = context.SocketPath }, CancellationToken.None)).ShouldBeTrue("worker B must re-open the run's recorded address, socket and all");

            (await child.AskAsync("call")).ShouldBe("200",
                $"the SAME child must reach worker B through the socket re-opened at the same path. If it cannot, the socket's directory was replaced rather than kept: the child's bind still points at the old inode. Compare `stat -c %i {context.SocketDirectory}` on the host with the directory the child sees. Child stderr: {child.Stderr}");
            upstreamB.Calls.ShouldBe(1, "and it was worker B that relayed it");

            await workerB.RevokeAsync(runId, "e2e-revoke", fencedToEpoch: null, CancellationToken.None);

            (await child.AskAsync("call")).ShouldStartWith("error:", customMessage: "a revoked lease's socket must answer nothing");
            upstreamB.Calls.ShouldBe(1, "and nothing more reached the provider");

            output.WriteLine($"{RanMarker} socket-channel uid={EffectiveUid()}");
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
    [InlineData(true)]    // the network-off run's sealed namespace — its one destination is this lease's port
    [InlineData(false)]   // an allowlist run's namespace with nothing allowed — reaches the worker through the same gateway
    public async Task A_namespaced_run_reaches_its_broker_and_is_refused_the_moment_the_lease_is_revoked(bool sealedToBroker)
    {
        if (!FilteredEgressNetns.IsSupported) return;   // no ip/nft (macOS dev / non-privileged) → the privileged CI job is authoritative

        var upstream = new AlwaysOkUpstream();
        using var broker = LoopbackModelCredentialBroker.ForTest(upstream);
        var runId = Guid.NewGuid();

        var brokered = await broker.OpenAsync(
            new() { RunId = runId, TeamId = Guid.NewGuid(), Epoch = 1, Upstream = new() { Provider = "Anthropic", ApiKey = "sk-e2e-upstream-key" }, Ttl = TimeSpan.FromMinutes(5) },
            CancellationToken.None);

        brokered.ShouldNotBeNull("the broker must be able to listen on a host that can build filtered-egress namespaces — a sealed run has no other route to a model");

        // Both production namespaces a brokered run is launched into: the sealed one, and the allowlist one, whose
        // broker is reached as a local delivery the forward filter never sees. See the class remarks.
        var netnsKey = Guid.NewGuid().ToString("N");
        var setup = sealedToBroker
            ? await FilteredEgressNetns.SetupSealedAsync(netnsKey, brokered!.RebindPort!.Value, timeoutSeconds: 20, CancellationToken.None)
            : await FilteredEgressNetns.SetupAsync(netnsKey, Array.Empty<string>(), timeoutSeconds: 20, CancellationToken.None);

        var plan = sealedToBroker ? "sealed" : "allowlist";
        var table = sealedToBroker ? $"inet {FilteredEgressPlan.NamespaceFor(netnsKey)}" : $"ip {FilteredEgressPlan.NamespaceFor(netnsKey)}";
        var why = sealedToBroker
            ? "a host-destined packet is INPUT, and the sealed input filter must admit exactly this lease's port"
            : "a host-destined packet is INPUT, which the allowlist plan's forward filter never sees, so no allowlist entry is needed";

        try
        {
            setup.SetupOk.ShouldBeTrue($"the {plan} netns must set up cleanly; setup error: {setup.SetupError}");
            setup.HostIp.ShouldNotBeNullOrWhiteSpace("the setup must report its gateway address — it is the only address a process inside the namespace can reach this worker at");

            // Resolve the broker's address through the PRODUCTION substitution the runner performs at launch, so the
            // URL the test curls is the one a real child would be handed.
            var url = ReachableUrl(brokered!, setup.HostIp!) + "/v1/messages";

            (await CurlInNetnsAsync(setup.ExecPrefix, url, brokered!.RunToken)).ShouldBe("200",
                customMessage: $"a run in the {plan} netns must reach its broker at {setup.HostIp} — if this is not 200, check by hand: `ip netns exec {FilteredEgressPlan.NamespaceFor(netnsKey)} curl -v {url}` and `nft list table {table}`. {why}");

            var relayedBeforeRevoke = upstream.Calls;

            await broker.RevokeAsync(runId, "e2e-revoke", fencedToEpoch: null, CancellationToken.None);

            // A revoke withdraws the ADDRESS, not just the routing entry: every lease owns its own listener, and
            // closing it is what stops one finished run from holding a port for the life of the worker. So what the
            // sealed process observes is a refused CONNECTION, not an HTTP 401 — a strictly stronger withdrawal, and
            // the shape this arm pins. (It used to read 401 back when one listener served every run and only the route
            // was removed.)
            var (exit, status) = await CurlAsync(setup.ExecPrefix, url, brokered.RunToken);

            exit.ShouldBe(CurlCouldNotConnect,
                customMessage: $"after a revoke nothing may answer at {url} from inside the namespace — curl must fail to connect (7), and got exit {exit} (status '{status}'). Exit 0 means something is STILL LISTENING on the revoked lease's port; exit 28 means the packet is being dropped rather than rejected — neither plan's filter drops this port, so that is a netns/filter change, not a brokerage one. Check by hand: `ip netns exec {FilteredEgressPlan.NamespaceFor(netnsKey)} curl -v {url}`");

            upstream.Calls.ShouldBe(relayedBeforeRevoke,
                "and nothing may reach the provider after the withdrawal — that, not which error the sandbox sees, is what decides whether a cancelled run can still spend the tenant's key");
        }
        finally { await FilteredEgressNetns.TeardownAsync(netnsKey, CancellationToken.None); }
    }

    /// <summary>curl's "Failed to connect to host" — what a sealed process gets once a revoked lease's listener is closed and its port stops existing.</summary>
    private const int CurlCouldNotConnect = 7;

    [Fact]
    public async Task A_sealed_run_reaches_its_broker_again_after_the_worker_that_minted_it_restarts()
    {
        if (!FilteredEgressNetns.IsSupported) return;   // no ip/nft (macOS dev / non-privileged) → the privileged CI job is authoritative

        var runId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var netnsKey = Guid.NewGuid().ToString("N");

        try
        {
            BrokeredModelCredential brokered;
            FilteredEgressNetns.SetupResult setup;
            string url;

            // Worker A mints the address, proves it works from inside the sealed namespace, and then GOES AWAY. The
            // namespace is sealed to the port worker A's lease holds — the port the re-bind below must take again.
            using (var workerA = LoopbackModelCredentialBroker.ForTest(new AlwaysOkUpstream()))
            {
                brokered = (await workerA.OpenAsync(LeaseFor(runId, teamId), CancellationToken.None)).ShouldNotBeNull();
                setup = await FilteredEgressNetns.SetupSealedAsync(netnsKey, brokered.RebindPort!.Value, timeoutSeconds: 20, CancellationToken.None);

                setup.SetupOk.ShouldBeTrue($"the sealed netns must set up cleanly; setup error: {setup.SetupError}");
                setup.HostIp.ShouldNotBeNullOrWhiteSpace("the setup must report its gateway address — it is the only address a process inside the namespace can reach this worker at");

                url = ReachableUrl(brokered, setup.HostIp!) + "/v1/messages";

                (await CurlInNetnsAsync(setup.ExecPrefix, url, brokered.RunToken)).ShouldBe("200", "precondition: the sealed run reaches its broker while the worker that minted it holds the address");
            }

            (await CurlAsync(setup.ExecPrefix, url, brokered.RunToken)).Exit.ShouldNotBe(0,
                "precondition: with worker A gone the address answers nothing at all — that is the deploy this test is about");

            using var workerB = LoopbackModelCredentialBroker.ForTest(new AlwaysOkUpstream());

            (await workerB.RebindAsync(RebindOf(brokered, runId, teamId, epoch: 2), CancellationToken.None)).ShouldBeTrue(
                "worker B must be able to re-open the address the sealed run is still calling");

            // The claim this lane exists for, and one no unit test can make: a re-bind has to take the WIDE address,
            // because a sealed child reaches this worker at its namespace GATEWAY and never on loopback. Fall back to
            // a loopback-only bind here and curl cannot connect at all — the re-bind reports success onto an address
            // nobody calls, which is strictly worse than the honest refusal it replaced.
            (await CurlInNetnsAsync(setup.ExecPrefix, url, brokered.RunToken)).ShouldBe("200",
                customMessage: $"a sealed run must reach its RE-BOUND broker at {setup.HostIp}. If curl cannot connect, the re-bind took loopback instead of the wide bind; check by hand: `ip netns exec {FilteredEgressPlan.NamespaceFor(netnsKey)} curl -v {url}`");
        }
        finally { await FilteredEgressNetns.TeardownAsync(netnsKey, CancellationToken.None); }
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

        var (exit, status) = await CurlAsync(null, $"https://{ProviderHost}/v1/messages", brokered.RunToken);

        if (exit != 0) return;   // no egress from this runner at all — nothing to observe, and the CI job with network is authoritative

        status.ShouldNotBe("200",
            customMessage: $"the per-run bearer must be worthless at {ProviderHost} — if it were accepted there, it would BE a provider credential and a token that escaped the sandbox would be a leak of one");
        int.Parse(status).ShouldBeGreaterThanOrEqualTo(400,
            customMessage: $"the provider must REFUSE the run token outright (got {status})");
    }

    private static string ReachableUrl(BrokeredModelCredential brokered, string? gatewayIp)
    {
        var spec = new SandboxSpec { Command = "curl", Environment = new Dictionary<string, string> { ["URL"] = brokered.BaseUrl } };

        return LocalProcessRunner.ResolveModelBrokerHost(spec, gatewayIp).Environment["URL"];
    }

    private static async Task<string> CurlInNetnsAsync(IReadOnlyList<string> execPrefix, string url, string token)
    {
        var (exit, status) = await CurlAsync(execPrefix, url, token);

        exit.ShouldBe(0, $"curl itself failed inside the namespace (exit {exit}) — that is a reachability failure, not a refusal. Diagnose with: `{string.Join(' ', execPrefix)} curl -v {url}`");

        return status;
    }

    /// <summary>POST to <paramref name="url"/> with the bearer and report (curl exit code, HTTP status). Run behind <paramref name="execPrefix"/> when one is given, so the request originates INSIDE the namespace.</summary>
    private static async Task<(int Exit, string Status)> CurlAsync(IReadOnlyList<string>? execPrefix, string url, string token)
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
