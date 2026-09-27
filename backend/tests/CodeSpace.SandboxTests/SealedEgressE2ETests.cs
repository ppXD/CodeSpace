using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Credentials.Broker;
using CodeSpace.Core.Services.Agents.Sandbox.Exceptions;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using Shouldly;
using Xunit.Abstractions;

namespace CodeSpace.SandboxTests;

/// <summary>
/// 🟢 Sandbox isolation E2E (high fidelity, Rule 12): a network-off run whose model is brokered, launched by the REAL
/// <see cref="LocalProcessRunner"/> under bubblewrap, reaches its broker and nothing else — and a probe from inside it,
/// through the real chain (prlimit → bwrap → <c>codespace-mcp relay</c> → python3), observes exactly one open door: the
/// real broker answers, through the relay on the sandbox's own loopback and the lease's socket bound read-only into it
/// (the child can neither delete that socket nor plant a file beside it), while the internet, DNS over TCP and UDP, the
/// worker's own address and its loopback listeners all stay shut. No
/// namespace of the worker's is built, so the same arms run as root and, from <see cref="NonRootWorkerE2ETests"/>, as the
/// shipped non-root worker, which may not build one.
///
/// <para>Needs bubblewrap, so it runs for real ONLY in the sandbox-isolation job; elsewhere it returns. The two arms
/// that stage host routing rules need root and run in the root lane alone. Every arm that ran prints
/// <see cref="RanMarker"/>, which the lane requires, so a silent return can never pass for coverage.</para>
/// </summary>
[Trait("Category", "Sandbox")]
public sealed class SealedEgressE2ETests(ITestOutputHelper output) : IDisposable
{
    /// <summary>Printed by every arm that actually ran; the sandbox lane requires one per arm in the test output.</summary>
    public const string RanMarker = "[sealed-egress-e2e] ran";

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    private readonly List<string> _dirs = [];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task A_network_off_brokered_run_reaches_its_broker_and_nothing_else(bool durable) => ReachesItsBrokerAndNothingElseAsync(durable, lane: "root");

    [Fact]
    public Task A_relayed_run_has_no_ipv6_path_to_the_worker_either() => HasNoIpv6PathToTheWorkerAsync(lane: "root");

    [Fact]
    public Task The_same_live_agent_reaches_the_next_worker_through_its_socket_after_a_restart() => ReachesTheNextWorkerAfterARestartAsync(lane: "root");

    [Fact]
    public Task A_brokered_child_the_worker_cannot_relay_is_refused_and_would_have_reached_nothing() => IsRefusedAndWouldHaveReachedNothingAsync(lane: "root");

    [Fact]
    public async Task A_host_policy_rule_that_discards_replies_from_the_broker_port_cannot_reach_a_relayed_run()
    {
        // The route check a namespace sealed through a veth once needed — a policy rule keyed on the protocol and the
        // broker's port discarded its replies after a clean setup — has nothing left to guard: a relayed run's broker
        // traffic never leaves loopback, which the kernel's local table answers before any rule is consulted. The rule
        // is staged for the lease's own port only, and the kernel's own lookup shows it would discard a reply to any
        // peer that is not local.
        if (!Confines() || !OperatingSystem.IsLinux() || NonRootWorker.EffectiveUid() != 0) return;

        using var run = await RelayedRunAsync();
        var table = RandomNumberGenerator.GetInt32(10_000, 1_000_000).ToString(CultureInfo.InvariantCulture);
        var port = run.Brokered.RebindPort!.Value.ToString(CultureInfo.InvariantCulture);
        string[] rule = ["pref", "100", "ipproto", "6", "sport", port, "lookup", table];

        string[] replyToAPeer = ["ip", "route", "get", "10.9.9.9", "ipproto", "6", "sport", port];

        try
        {
            (await RunHostExitAsync(replyToAPeer)).ShouldBe(0, "control: before the rule, this host routes a reply from the broker's port to a peer that is not local");
            (await RunHostExitAsync(["ip", "route", "add", "unreachable", "default", "table", table])).ShouldBe(0, "setup: the null route must be installable in its own table");
            (await RunHostExitAsync(["ip", "rule", "add", .. rule])).ShouldBe(0, "setup: the rule that consults it before main must be installable");
            (await RunHostExitAsync(replyToAPeer)).ShouldNotBe(0, "control: with the rule, that reply is discarded — which is what it did to a veth-sealed run's broker replies");

            var (result, probe) = await RunAsync(run.Spec);

            result.Status.ShouldBe(SandboxStatus.Success, $"the probe must run to its end; stderr: {result.Stderr}");
            probe["broker"].ShouldBe("200", $"the relayed call is answered: loopback is local, and no policy rule is consulted before it; probe: {Describe(probe)}");

            output.WriteLine($"{RanMarker} relay-policy-route broker={probe["broker"]}");
        }
        finally
        {
            await RunHostExitAsync(["ip", "rule", "del", .. rule]);
            await RunHostExitAsync(["ip", "route", "flush", "table", table]);
        }
    }

    /// <summary>The main arm, for either lane: see the class remarks.</summary>
    internal async Task ReachesItsBrokerAndNothingElseAsync(bool durable, string lane)
    {
        if (!Confines()) return;

        using var run = await RelayedRunAsync();
        using var otherListener = new TcpListener(IPAddress.Any, 0);
        otherListener.Start();

        var spec = run.Spec with
        {
            Environment = new Dictionary<string, string>(run.Spec.Environment)
            {
                ["OTHER_PORT"] = ((IPEndPoint)otherListener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture),
                ["WORKER_IP"] = WorkerIpv4(),
                ["SOCK_PATH"] = run.SocketPath,
                // An egress proxy the worker (or the task) carries, as a proxied deployment would: a network-off child
                // cannot reach it, so the launch must drop it — which the probe reports directly, since the broker's
                // NO_PROXY exemption would let urllib past it even if the drop were gone.
                ["HTTP_PROXY"] = "http://10.255.255.1:3128", ["http_proxy"] = "http://10.255.255.1:3128",
            },
        };

        var (result, probe) = durable ? await RunDurableAsync(spec) : await RunAsync(spec);

        result.Status.ShouldBe(SandboxStatus.Success, $"the probe itself must run to its end inside the sandbox; stderr: {result.Stderr}");
        probe["broker"].ShouldBe("200", $"the run's own broker is the one destination a network-off run must reach — through the relay and its socket, not through the proxy it was handed. Check the socket with `ls -la {Path.GetDirectoryName(run.SocketPath)}`; probe: {Describe(probe)}");
        probe["proxies"].ShouldBe("none", $"a sealed launch drops the proxy variables outright; probe: {Describe(probe)}");
        probe["internet"].ShouldNotBe("open", $"a network-off run must not reach the internet; probe: {Describe(probe)}");
        probe["dns_tcp"].ShouldNotBe("open", $"no DNS over TCP — a resolver is a tunnel; probe: {Describe(probe)}");
        probe["dns_udp"].ShouldNotBe("answered", $"no DNS over UDP either; probe: {Describe(probe)}");
        probe["worker_eth0"].ShouldNotBe("open", $"the worker's own address (its API, every other run's lease) must stay shut; probe: {Describe(probe)}");
        probe["host_loopback"].ShouldNotBe("open", $"and so must the worker's loopback listeners: the child's 127.0.0.1 is its own, where only the relay listens; probe: {Describe(probe)}");
        probe["links"].Split(',').ShouldNotContain(link => link.StartsWith("eth", StringComparison.Ordinal) || link.StartsWith("cs", StringComparison.Ordinal) || link.StartsWith("veth", StringComparison.Ordinal), $"no interface but loopback and the kernel's own tunnels: {probe["links"]}");
        AssertSocketDirectoryIsReadOnly(probe["sock_unlink"], probe["sock_plant"], run.SocketPath);

        await run.Broker.RevokeAsync(run.RunId, "e2e-revoke", fencedToEpoch: null, CancellationToken.None);

        File.Exists(run.SocketPath).ShouldBeFalse("a revoked lease's socket goes with it");
        Directory.Exists(Path.GetDirectoryName(run.SocketPath)).ShouldBeTrue("its directory stays, since a running sandbox's bind pins it");

        output.WriteLine($"{RanMarker} {(lane == "root" ? "" : lane + " ")}{(durable ? "durable" : "non-durable")} uid={NonRootWorker.EffectiveUid()} {Describe(probe)}");
    }

    /// <summary>The IPv6 arm, for either lane: the relayed child has no v6 path to the worker — not over loopback, which is its own, nor to any address the worker holds, which it has no route to.</summary>
    internal async Task HasNoIpv6PathToTheWorkerAsync(string lane)
    {
        if (!Confines()) return;

        using var listener = new TcpListener(IPAddress.IPv6Any, 0);
        listener.Server.DualMode = false;

        try { listener.Start(); }
        catch (SocketException)
        {
            output.WriteLine($"[sealed-egress-e2e] skipped relay-ipv6 (IPv6 is disabled on this host, so there is no v6 path to close)");
            return;
        }

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var addresses = WorkerIpv6Addresses();

        (await ConnectsAsync(IPAddress.IPv6Loopback, port)).ShouldBeTrue("control: the worker's own v6 listener answers on [::1], or a refusal inside proves nothing about the sandbox");

        using var run = await RelayedRunAsync();
        var spec = run.Spec with
        {
            Args = ["-c", Ipv6ProbeScript],
            Environment = new Dictionary<string, string>(run.Spec.Environment) { ["V6_PORT"] = port.ToString(CultureInfo.InvariantCulture), ["V6_TARGETS"] = string.Join(',', addresses.Prepend("::1")) },
        };

        var (result, probe) = await RunAsync(spec);

        result.Status.ShouldBe(SandboxStatus.Success, $"the probe must run to its end; stderr: {result.Stderr}");
        probe["broker"].ShouldBe("200", $"control: the relay itself works in this sandbox; probe: {Describe(probe)}");
        probe.Where(pair => pair.Key.StartsWith("v6 ", StringComparison.Ordinal)).ShouldAllBe(pair => pair.Value != "open", $"no v6 address of the worker is reachable from inside; probe: {Describe(probe)}");

        output.WriteLine($"{RanMarker} {(lane == "root" ? "" : lane + " ")}relay-ipv6 targets={addresses.Count + 1} {Describe(probe)}");
    }

    /// <summary>
    /// The restart arm, for either lane, on a REAL durable launch: the agent is started once and kept; worker A's broker
    /// serves it through the relay, goes away, and worker B re-binds the run's recorded address and re-opens its socket
    /// at the same path. The same process — same pid, same relay — reaches worker B, because the socket's directory was
    /// never replaced and the relay opens a fresh connection to the socket for every call.
    /// </summary>
    internal async Task ReachesTheNextWorkerAfterARestartAsync(string lane)
    {
        if (!Confines()) return;

        var runId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var socketPath = AgentRunExecutor.ModelBrokerSocketPathFor(new AgentPermissions { Network = AgentNetworkAccess.Off }, runId).ShouldNotBeNull("the executor mints a socket for a network-off run on Linux");
        var workspace = NewDirectory("restart-ws");
        var workerA = LoopbackModelCredentialBroker.ForTest(new CountingUpstream());
        var upstreamB = new CountingUpstream();

        try
        {
            var brokered = (await workerA.OpenAsync(Lease(runId, teamId, socketPath), CancellationToken.None)).ShouldNotBeNull();
            var spec = AgentRunExecutor.ApplyModelBrokerChannel(new SandboxSpec
            {
                Command = "/usr/bin/python3", Args = ["-u", "-c", RestartProbeScript], WorkingDirectory = workspace, TimeoutSeconds = 120,
                Environment = new Dictionary<string, string> { ["BROKER_URL"] = brokered.BaseUrl, ["RUN_TOKEN"] = brokered.RunToken, ["SIGNAL_DIR"] = workspace },
            }, brokered);

            var key = Guid.NewGuid().ToString("N");
            var runner = new LocalProcessRunner();
            var handle = await runner.LaunchAsync(spec, key, CancellationToken.None);
            _dirs.Add(handle.SpoolDirectory);

            var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var attach = runner.AttachAsync(handle, (frame, _) => { lines.Enqueue(frame.Text); return Task.CompletedTask; }, CancellationToken.None);

            var first = await CallAsync(workspace, lines, 1);
            first.ShouldStartWith("call1=200", customMessage: "worker A answers the relayed agent while it holds the address");

            workerA.Dispose();
            (await CallAsync(workspace, lines, 2)).ShouldStartWith("call2=error", customMessage: "with worker A gone the agent's call must fail — that is the restart this arm is about");

            using var workerB = LoopbackModelCredentialBroker.ForTest(upstreamB, logger: new TestOutputLogger<LoopbackModelCredentialBroker>(output));
            (await workerB.RebindAsync(RebindOf(brokered, runId, teamId, epoch: 2) with { SocketPath = socketPath }, CancellationToken.None)).ShouldBeTrue("worker B must re-open the run's recorded address, socket and all");

            var third = await CallAsync(workspace, lines, 3);
            third.ShouldStartWith("call3=200", customMessage: $"the SAME agent must reach worker B through the socket re-opened at the same path; if not, compare `stat -c %i {Path.GetDirectoryName(socketPath)}` with what its sandbox binds");
            Pid(third).ShouldBe(Pid(first), "it is the same live agent, not a relaunch");
            upstreamB.Calls.ShouldBe(1, "and it was worker B that relayed it");

            await File.WriteAllTextAsync(Path.Combine(workspace, "stop"), "");
            (await attach.WaitAsync(Deadline)).Status.ShouldBe(SandboxStatus.Success, "the agent ends on its own once released");
            handle.EgressNetnsKey.ShouldBeNull("no namespace of the worker's was built for it");

            output.WriteLine($"{RanMarker} {(lane == "root" ? "" : lane + " ")}restart uid={NonRootWorker.EffectiveUid()} pid={Pid(first)}");
        }
        finally { workerA.Dispose(); }
    }

    /// <summary>
    /// The refusal arm, for either lane: the REAL runner, on this confining host, refuses a network-off brokered spec it
    /// could not carry to its broker — the lease came back without a socket, or the helper is not there — and the same
    /// spec launched anyway, as nothing refused it before, really is cut off: its call to its broker reaches nothing.
    /// </summary>
    internal async Task IsRefusedAndWouldHaveReachedNothingAsync(string lane)
    {
        if (!Confines()) return;

        using var broker = LoopbackModelCredentialBroker.ForTest(new CountingUpstream());
        var runId = Guid.NewGuid();
        var brokered = (await broker.OpenAsync(Lease(runId, Guid.NewGuid(), socketPath: null), CancellationToken.None)).ShouldNotBeNull();
        var spec = AgentRunExecutor.ApplyModelBrokerChannel(ProbeSpec(brokered), brokered);

        spec.ModelBrokerPort.ShouldNotBeNull("fixture check: a network-off brokered spec, stamped by the executor's own hardening");

        var noSocket = Should.Throw<SealedEgressUnavailableException>(() => new LocalProcessRunner().EnsureEgressAdmissible(spec));
        noSocket.Cause.ShouldBe(SealedEgressUnavailableException.CauseBrokerSocketUnavailable);
        ((CodeSpace.Messages.Failures.IFailure)noSocket).Code.ShouldBe(CodeSpace.Messages.Failures.FailureCodes.SandboxSealedEgressUnavailable);

        var missing = Path.Combine(NewDirectory("no-helper"), "codespace-mcp");
        Should.Throw<SealedEgressUnavailableException>(() => LocalProcessRunner.EnsureEgressAdmissible(spec with { ModelBrokerSocketPath = "/spool/k/broker/seg/s" }, BubblewrapSandbox.Available is not null, missing))
            .Cause.ShouldStartWith(SealedEgressUnavailableException.CauseRelayMissing);

        var (result, probe) = await RunAsync(spec);

        result.Status.ShouldBe(SandboxStatus.Success, $"the probe must run to its end; stderr: {result.Stderr}");
        probe["broker"].ShouldStartWith("URLError", customMessage: $"launched without the relay, the child reaches no broker at all — which is what the refusal spares it; probe: {Describe(probe)}");

        output.WriteLine($"{RanMarker} {(lane == "root" ? "" : lane + " ")}relay-refused uid={NonRootWorker.EffectiveUid()} cause={noSocket.Cause}");
    }

    public void Dispose()
    {
        foreach (var dir in _dirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup of a scratch or spool dir */ }
        }
    }

    /// <summary>
    /// Python defining <c>sock_writes(sock)</c>: from inside the sandbox, try to delete the lease's socket and to plant a
    /// file beside it, and return each attempt's errno name (<c>ok</c> if it worked) — the two writes a read-only bind of
    /// the socket's directory must refuse. Shared with the allowlist arm, which binds the same directory.
    /// </summary>
    internal const string SocketDirectoryWrites = """
        def sock_writes(sock):
            import errno, os
            def attempt(write):
                try:
                    write(); return 'ok'
                except OSError as e:
                    return errno.errorcode.get(e.errno, str(e.errno))
            def plant():
                open(os.path.join(os.path.dirname(sock), 'planted'), 'w').close()
            return attempt(lambda: os.unlink(sock)), attempt(plant)
        """;

    /// <summary>
    /// The socket's directory was bound READ-ONLY into the sandbox (<see cref="LocalProcessRunner.PlanFor"/>): the child
    /// connected through it, but could neither delete the worker's socket nor plant a file in the worker's directory —
    /// read off both sides of the bind, the errno the child saw and what the host still has.
    /// </summary>
    internal static void AssertSocketDirectoryIsReadOnly(string unlink, string plant, string socketPath)
    {
        var directory = Path.GetDirectoryName(socketPath)!;

        unlink.ShouldBe("EROFS", $"the child must not be able to delete the worker's socket: its directory is bound read-only. Check the launch's bwrap argv binds {directory} with --ro-bind, not --bind");
        plant.ShouldBe("EROFS", "nor plant a file in the worker's socket directory");
        File.Exists(socketPath).ShouldBeTrue("the worker's socket is still there on the host");
        File.Exists(Path.Combine(directory, "planted")).ShouldBeFalse("and nothing was planted beside it on the host");
    }

    /// <summary>Probes every door from inside the run and prints one JSON object of what each did. It never fails on a shut door — the test decides.</summary>
    private const string ProbeScript = SocketDirectoryWrites + "\n" + """
        import json, os, socket, urllib.request
        res = {}
        url = os.environ['BROKER_URL']
        req = urllib.request.Request(url + '/v1/messages', data=b'{}', method='POST', headers={'Authorization': 'Bearer ' + os.environ['RUN_TOKEN'], 'content-type': 'application/json'})
        try:
            res['broker'] = str(urllib.request.urlopen(req, timeout=10).status)
        except Exception as e:
            res['broker'] = type(e).__name__ + ':' + str(e)
        if 'SOCK_PATH' in os.environ:
            res['sock_unlink'], res['sock_plant'] = sock_writes(os.environ['SOCK_PATH'])
        def tcp(host, port):
            try:
                s = socket.create_connection((host, port), timeout=3); s.close(); return 'open'
            except OSError as e:
                return type(e).__name__
        def udp(host, port):
            try:
                s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); s.settimeout(3)
                s.sendto(bytes.fromhex('123401000001000000000000076578616d706c6503636f6d0000010001'), (host, port)); s.recvfrom(512); return 'answered'
            except OSError as e:
                return type(e).__name__
        res['internet'] = tcp('1.1.1.1', 80)
        res['dns_tcp'] = tcp('8.8.8.8', 53)
        res['dns_udp'] = udp('8.8.8.8', 53)
        if 'OTHER_PORT' in os.environ:
            res['worker_eth0'] = tcp(os.environ['WORKER_IP'], int(os.environ['OTHER_PORT']))
            res['host_loopback'] = tcp('127.0.0.1', int(os.environ['OTHER_PORT']))
        res['links'] = ','.join(sorted(name for _, name in socket.if_nameindex()))
        res['proxies'] = ','.join(sorted(n for n in os.environ if n.lower() in ('http_proxy', 'https_proxy', 'all_proxy'))) or 'none'
        print(json.dumps(res))
        """;

    /// <summary>The broker through the relay, then every IPv6 target the worker holds, from inside the sandbox.</summary>
    private const string Ipv6ProbeScript = """
        import json, os, socket, urllib.request
        res = {}
        req = urllib.request.Request(os.environ['BROKER_URL'] + '/v1/messages', data=b'{}', method='POST', headers={'Authorization': 'Bearer ' + os.environ['RUN_TOKEN'], 'content-type': 'application/json'})
        try:
            res['broker'] = str(urllib.request.urlopen(req, timeout=10).status)
        except Exception as e:
            res['broker'] = type(e).__name__ + ':' + str(e)
        port = int(os.environ['V6_PORT'])
        for target in os.environ['V6_TARGETS'].split(','):
            try:
                s = socket.create_connection((target, port), timeout=3); s.close(); res['v6 ' + target] = 'open'
            except OSError as e:
                res['v6 ' + target] = type(e).__name__
        print(json.dumps(res))
        """;

    /// <summary>A kept agent: each time <c>go&lt;n&gt;</c> appears in its workspace it makes one broker call and prints the outcome with its pid; <c>stop</c> ends it.</summary>
    private const string RestartProbeScript = """
        import os, sys, time, urllib.request
        url, tok, d = os.environ['BROKER_URL'] + '/v1/messages', os.environ['RUN_TOKEN'], os.environ['SIGNAL_DIR']
        def call():
            req = urllib.request.Request(url, data=b'{}', method='POST', headers={'Authorization': 'Bearer ' + tok, 'content-type': 'application/json'})
            try:
                return str(urllib.request.urlopen(req, timeout=10).status)
            except Exception as e:
                return 'error:' + type(e).__name__
        n = 0
        while True:
            n += 1
            while not os.path.exists(os.path.join(d, 'go%d' % n)):
                if os.path.exists(os.path.join(d, 'stop')):
                    sys.exit(0)
                time.sleep(0.1)
            print('call%d=%s pid=%d' % (n, call(), os.getpid()), flush=True)
        """;

    /// <summary>A lane that confines has bubblewrap and the helper beside this assembly; there a missing one is a failure, not a skip.</summary>
    private static bool Confines()
    {
        if (BubblewrapSandbox.Available is null)
        {
            BubblewrapSandbox.IsRequired.ShouldBeFalse("Sandbox:RequireConfinement is set but this host cannot sandbox (bwrap/userns) — the E2E cannot prove how a network-off run reaches its broker here");
            return false;
        }

        File.Exists(LocalProcessRunner.McpProxyBinaryPath()).ShouldBeTrue($"the codespace-mcp helper that runs the relay must be at {LocalProcessRunner.McpProxyBinaryPath()} — the test project's build copies it there");
        return true;
    }

    /// <summary>A network-off run's lease with a socket at the path the executor mints, and the spec the executor would stamp from it.</summary>
    private async Task<RelayedRun> RelayedRunAsync()
    {
        var runId = Guid.NewGuid();
        var socketPath = AgentRunExecutor.ModelBrokerSocketPathFor(new AgentPermissions { Network = AgentNetworkAccess.Off }, runId).ShouldNotBeNull("the executor mints a socket for a network-off run on Linux");
        var broker = LoopbackModelCredentialBroker.ForTest(new CountingUpstream());
        var brokered = (await broker.OpenAsync(Lease(runId, Guid.NewGuid(), socketPath), CancellationToken.None)).ShouldNotBeNull("the broker must be able to lease on this host");

        brokered.SocketPath.ShouldBe(socketPath, $"the lease must bind its socket; check `ls -la {Path.GetDirectoryName(socketPath)}`");
        _dirs.Add(Path.GetDirectoryName(socketPath)!);

        return new RelayedRun(runId, socketPath, broker, brokered, AgentRunExecutor.ApplyModelBrokerChannel(ProbeSpec(brokered), brokered));
    }

    private static SandboxSpec ProbeSpec(BrokeredModelCredential brokered) => new()
    {
        Command = "/usr/bin/python3", Args = ["-c", ProbeScript], AllowNetwork = false, TimeoutSeconds = 60,
        Environment = new Dictionary<string, string> { ["BROKER_URL"] = brokered.BaseUrl, ["RUN_TOKEN"] = brokered.RunToken },
    };

    private async Task<(SandboxResult Result, Dictionary<string, string> Probe)> RunDurableAsync(SandboxSpec spec)
    {
        var key = Guid.NewGuid().ToString("N");
        var runner = new LocalProcessRunner();
        var lines = new List<string>();

        var handle = await runner.LaunchAsync(spec, key, CancellationToken.None);
        _dirs.Add(handle.SpoolDirectory);

        handle.EgressNetnsKey.ShouldBeNull("a relayed network-off run gets no namespace of the worker's, so there is nothing to tear down by name");
        var confinement = handle.Confinement.ShouldNotBeNull("the launch must record what confinement it applied");
        confinement.EgressSealedToBroker.ShouldBeTrue("the record must say the run was sealed to its broker, not merely severed");
        confinement.NetworkSevered.ShouldBeTrue("a sealed run is severed from everything else");

        var result = await runner.AttachAsync(handle, (frame, _) => { lines.Add(frame.Text); return Task.CompletedTask; }, CancellationToken.None);

        return (result, ParseProbe(string.Join('\n', lines)));
    }

    private static async Task<(SandboxResult Result, Dictionary<string, string> Probe)> RunAsync(SandboxSpec spec)
    {
        var result = await new LocalProcessRunner().RunAsync(spec, CancellationToken.None);

        return (result, ParseProbe(result.Stdout));
    }

    private static Dictionary<string, string> ParseProbe(string stdout)
    {
        var line = stdout.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{'));

        return line is null ? new Dictionary<string, string> { ["broker"] = $"no probe output: {stdout}", ["internet"] = "?", ["dns_tcp"] = "?", ["dns_udp"] = "?", ["worker_eth0"] = "?", ["host_loopback"] = "?", ["links"] = "?", ["proxies"] = "?", ["sock_unlink"] = "?", ["sock_plant"] = "?" } : JsonSerializer.Deserialize<Dictionary<string, string>>(line)!;
    }

    private static string Describe(Dictionary<string, string> probe) => string.Join(' ', probe.Select(p => $"{p.Key}={p.Value}"));

    /// <summary>Signal call <paramref name="n"/> and wait for the agent's line for it — bounded, naming the call (Rule 12.10).</summary>
    private static async Task<string> CallAsync(string workspace, System.Collections.Concurrent.ConcurrentQueue<string> lines, int n)
    {
        await File.WriteAllTextAsync(Path.Combine(workspace, $"go{n}"), "");

        var watch = Stopwatch.StartNew();

        while (watch.Elapsed < Deadline)
        {
            if (lines.FirstOrDefault(line => line.StartsWith($"call{n}=", StringComparison.Ordinal)) is { } line) return line;
            await Task.Delay(100);
        }

        throw new Xunit.Sdk.XunitException($"the kept agent never printed call{n} within {Deadline.TotalSeconds}s; lines so far: {string.Join(" | ", lines)}");
    }

    private static string Pid(string line) => line.Split(" pid=")[1];

    /// <summary>The worker's first non-loopback IPv4 address — its eth0, where the worker's own listeners are reachable from its network.</summary>
    private static string WorkerIpv4() =>
        NetworkInterface.GetAllNetworkInterfaces().Where(nic => nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses).Select(address => address.Address).FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork)?.ToString()
        ?? throw new Xunit.Sdk.XunitException("fixture: this host has no non-loopback IPv4 address to probe the worker at");

    /// <summary>Every non-loopback IPv6 address the worker holds, link-local ones with their scope — none may be reachable from a relayed sandbox.</summary>
    private static IReadOnlyList<string> WorkerIpv6Addresses() =>
        NetworkInterface.GetAllNetworkInterfaces().Where(nic => nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses.Select(address => (nic.Name, address.Address)))
            .Where(pair => pair.Address.AddressFamily == AddressFamily.InterNetworkV6)
            .Select(pair => pair.Address.IsIPv6LinkLocal ? $"{pair.Address.ToString().Split('%')[0]}%{pair.Name}" : pair.Address.ToString()).ToList();

    private static async Task<bool> ConnectsAsync(IPAddress address, int port)
    {
        using var client = new TcpClient(address.AddressFamily);

        try { await client.ConnectAsync(address, port).WaitAsync(TimeSpan.FromSeconds(3)); return true; }
        catch (Exception) { return false; }
    }

    private string NewDirectory(string label)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cs-sealed-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        _dirs.Add(directory);
        return directory;
    }

    private static ModelCredentialLeaseRequest Lease(Guid runId, Guid teamId, string? socketPath) =>
        new() { RunId = runId, TeamId = teamId, Epoch = 1, Upstream = new() { Provider = "Anthropic", ApiKey = "sk-sealed-e2e-upstream" }, Ttl = TimeSpan.FromMinutes(5), SocketPath = socketPath };

    /// <summary>The re-bind the next worker builds from what the run's durable handle carries.</summary>
    private static ModelCredentialRebindRequest RebindOf(BrokeredModelCredential brokered, Guid runId, Guid teamId, long epoch) => new()
    {
        RunId = runId, TeamId = teamId, Epoch = epoch, Port = brokered.RebindPort!.Value, PathId = brokered.RebindRoute!,
        RunToken = brokered.RunToken, Upstream = new() { Provider = "Anthropic", ApiKey = "sk-sealed-e2e-upstream" }, Ttl = TimeSpan.FromMinutes(5),
    };

    private static async Task<int> RunHostExitAsync(IReadOnlyList<string> argv)
    {
        var psi = new ProcessStartInfo { FileName = argv[0], UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in argv.Skip(1)) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        await process.WaitForExitAsync();

        return process.ExitCode;
    }

    /// <summary>A network-off run's lease, the broker holding it, and the spec the executor stamps from it.</summary>
    private sealed record RelayedRun(Guid RunId, string SocketPath, LoopbackModelCredentialBroker Broker, BrokeredModelCredential Brokered, SandboxSpec Spec) : IDisposable
    {
        public void Dispose() => Broker.Dispose();
    }

    /// <summary>The provider, answering 200 to anything the broker relays and counting it — this lane asserts reachability, never what a model said.</summary>
    private sealed class CountingUpstream : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") });
        }
    }
}
