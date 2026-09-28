using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Credentials.Broker;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using Shouldly;
using Xunit.Abstractions;

namespace CodeSpace.SandboxTests;

/// <summary>
/// 🟢 Sandbox isolation E2E (high fidelity, Rule 12): drives the REAL durable launch (B3.2b) — not the
/// <see cref="FilteredEgressNetns"/> executor directly — end to end. A <see cref="SandboxSpec"/> carrying a
/// deny-by-default egress allowlist is launched via <see cref="LocalProcessRunner.LaunchAsync"/>, which sets up a
/// filtered network namespace and runs the WHOLE supervisor chain (<c>ip netns exec</c> → [prlimit] → [bwrap] →
/// <c>curl</c>) inside it. Proves three durable-launch guarantees against a live kernel: (1) an ALLOWED IP is
/// reachable from inside the launched run, (2) a NON-allowed IP is DROPPED, and (3) the netns is REAPED on the
/// run's terminal path (no leak). Needs ip + nft + CAP_NET_ADMIN, so it runs for real ONLY in the privileged
/// sandbox-isolation CI job; elsewhere <see cref="FilteredEgressNetns.IsSupported"/> is false and it degrade-skips.
/// Uses raw IPs over plain HTTP so the signal is purely the egress filter — not DNS, not TLS. The arm for a worker
/// that has the binaries but cannot filter (<see cref="IsSeveredAndStillReachesItsBrokerAsync"/>) is run by the
/// non-root lane, which is that posture.
/// </summary>
[Trait("Category", "Sandbox")]
public sealed class DurableLaunchEgressE2ETests(ITestOutputHelper output)
{
    private const string Allowed = "1.1.1.1";   // Cloudflare — allowlisted
    private const string Denied = "8.8.8.8";    // Google — NOT allowlisted, must be dropped

    [Fact]
    public async Task The_durable_launch_runs_the_agent_inside_the_filtered_netns_and_reaps_it()
    {
        if (!FilteredEgressNetns.IsSupported) return;   // no ip/nft (macOS dev / non-privileged) → the privileged CI job is authoritative

        var runner = new LocalProcessRunner();

        // ALLOWED: curl the allowlisted IP through the REAL durable launch. The run is launched INSIDE the netns, so a
        // success proves the launched chain (netns → [prlimit] → [bwrap] → curl) inherited the allowlist-filtered egress.
        var allowKey = Guid.NewGuid().ToString("N");
        var allow = await DurableCurlAsync(runner, allowKey, allow: Allowed, target: Allowed);
        allow.Status.ShouldBe(SandboxStatus.Success, $"the ALLOWED host {Allowed} must be reachable from inside the launched netns. Stderr: {allow.Stderr}");

        // The netns the run was launched inside is torn down on the terminal path — assert it is GONE (no leak).
        (await NetnsExistsAsync(NamespaceOf(allowKey))).ShouldBeFalse("the run's filtered netns must be reaped on completion — a leak would survive here");

        // DENIED: a NON-allowlisted host is dropped by the netns → curl times out → the run completes Failed.
        var denyKey = Guid.NewGuid().ToString("N");
        var deny = await DurableCurlAsync(runner, denyKey, allow: Allowed, target: Denied);
        deny.Status.ShouldBe(SandboxStatus.Failed, $"a NON-allowed host ({Denied}) must be DROPPED by the launched netns — the deny-by-default filter is the whole point. If this succeeds, the filter is not enforcing inside the durable launch.");

        (await NetnsExistsAsync(NamespaceOf(denyKey))).ShouldBeFalse("the denied run's netns is reaped on the terminal path too");
    }

    [Fact]
    public async Task An_allowlist_run_reaches_its_broker_through_the_relay_and_its_allowlist_still_holds()
    {
        // An allowlist run's namespace is its own, so its lease's loopback port is not its loopback: it reaches its
        // broker through the relay in front of its CLI and the lease's socket — which bubblewrap, sharing the filtered
        // namespace, binds read-only — while the allowlist that namespace enforces decides everything else. The worker
        // itself is not on it: a listener on every address the worker has stays shut at the run's gateway and at the
        // worker's own address, which the guard on the run's veth drops.
        if (!FilteredEgressNetns.IsSupported || BubblewrapSandbox.Available is null) return;   // the root lane, with ip, nft and bwrap, is authoritative

        FilteredEgressNetns.CanFilter.ShouldBeTrue($"ip and nft are here and this is the root lane, so an allowlist must be filterable here ({FilteredEgressNetns.FilterUnavailableReason}); otherwise it is severed, which the non-root lane pins");

        using var workerListener = new TcpListener(IPAddress.Any, 0);
        workerListener.Start();

        using var broker = LoopbackModelCredentialBroker.ForTest(new OkUpstream());
        var (spec, socketPath) = await BrokeredAllowlistSpecAsync(broker, workerListener);
        var key = Guid.NewGuid().ToString("N");
        var runner = new LocalProcessRunner();
        var lines = new List<string>();

        try
        {
            var handle = await runner.LaunchAsync(spec, key, CancellationToken.None);
            handle.EgressNetnsKey.ShouldBe(key, "an enforceable allowlist still launches the run inside its filtered netns");
            handle.Confinement.ShouldNotBeNull().NetworkSevered.ShouldBeFalse("a filtered allowlist run shares its namespace's network: filtered, not severed");
            handle.Confinement.EgressSealedToBroker.ShouldBeFalse("an allowlist run is filtered, not sealed: it has more than one destination");

            var result = await runner.AttachAsync(handle, (frame, _) => { lines.Add(frame.Text); return Task.CompletedTask; }, CancellationToken.None);
            var probe = string.Join(' ', lines);

            result.Status.ShouldBe(SandboxStatus.Success, $"the probe must run to its end; stderr: {result.Stderr}");
            probe.ShouldContain("broker=200", customMessage: $"the allowlist run's broker answers through the relay; check `ls -la {Path.GetDirectoryName(socketPath)}`; probe: {probe}");
            probe.ShouldContain("allowed=open", customMessage: $"the allowlisted IP is still reachable through the namespace's NAT; probe: {probe}");
            IsInThePool(ProbeValue(probe, "src")).ShouldBeTrue($"the run's namespace end must hold an address from 198.19.64.0–198.19.191.255, and it is that source the NAT carried to {Allowed}; check `ip netns exec {NamespaceOf(key)} ip -4 addr` while a run is up; probe: {probe}");
            probe.ShouldNotContain("denied=open", customMessage: $"and a host outside the allowlist is still dropped; probe: {probe}");
            ProbeValue(probe, "gateway").ShouldNotBe("open", $"the worker's listener must stay shut at the run's gateway — the guard on its veth drops it; probe: {probe}");
            ProbeValue(probe, "worker").ShouldNotBe("open", $"and at the worker's own address; probe: {probe}");
            SealedEgressE2ETests.AssertSocketDirectoryIsReadOnly(ProbeValue(probe, "sock_unlink"), ProbeValue(probe, "sock_plant"), socketPath);

            output.WriteLine($"[durable-egress-e2e] ran allowlist-relay {probe}");
        }
        finally
        {
            try { Directory.Delete(LocalProcessRunner.SpoolDirectoryFor(key), recursive: true); } catch { /* best-effort */ }
        }

        (await NetnsExistsAsync(NamespaceOf(key))).ShouldBeFalse("the run's filtered netns is reaped on completion");
    }

    /// <summary>
    /// The severed arm, for the non-root lane (<see cref="NonRootWorkerE2ETests"/>): a confining worker with <c>ip</c>
    /// and <c>nft</c> installed that cannot filter — the shipped image's non-root posture — launches an allowlist run
    /// severed, never into a namespace its setup would refuse after the run was admitted to spend (where nothing
    /// confines, <see cref="UnconfinedWorkerE2ETests"/> pins the other answer). The REAL runner
    /// admits it, launches it with no namespace of the worker's and records it severed, and the probe inside it still
    /// reaches its broker through the relay while the allowlisted IP is as unreachable as any other.
    /// </summary>
    internal async Task IsSeveredAndStillReachesItsBrokerAsync(string lane)
    {
        FilteredEgressNetns.IsSupported.ShouldBeTrue("fixture check: ip and nft are installed here, so the binaries alone would plan this run Filtered — the posture whose setup aborted it after its spend was admitted");
        FilteredEgressNetns.CanFilter.ShouldBeFalse("this lane cannot filter an allowlist, which is the posture this arm is for");

        using var workerListener = new TcpListener(IPAddress.Any, 0);
        workerListener.Start();

        using var broker = LoopbackModelCredentialBroker.ForTest(new OkUpstream());
        var (spec, socketPath) = await BrokeredAllowlistSpecAsync(broker, workerListener);
        var key = Guid.NewGuid().ToString("N");
        var runner = new LocalProcessRunner();
        var lines = new List<string>();

        Should.NotThrow(() => runner.EnsureEgressAdmissible(spec), "a severed allowlist run whose lease has its socket and whose helper runs the relay is admitted");

        try
        {
            // Keyed on the binaries alone, the launch plans this run Filtered and throws here: its namespace setup is refused.
            var handle = await runner.LaunchAsync(spec, key, CancellationToken.None);
            handle.EgressNetnsKey.ShouldBeNull("an allowlist this worker cannot filter gets no namespace: bubblewrap severs it instead");

            var record = handle.Confinement.ShouldNotBeNull();
            record.Outcome.ShouldBe(SandboxConfinementOutcome.Confined);
            record.NetworkSevered.ShouldBeTrue("an allowlist this worker cannot filter FAILS CLOSED to no egress, and the run's record says so");

            var result = await runner.AttachAsync(handle, (frame, _) => { lines.Add(frame.Text); return Task.CompletedTask; }, CancellationToken.None);
            var probe = string.Join(' ', lines);

            result.Status.ShouldBe(SandboxStatus.Success, $"the probe must run to its end; stderr: {result.Stderr}");
            probe.ShouldContain("broker=200", customMessage: $"the severed run still reaches its broker through the relay; check `ls -la {Path.GetDirectoryName(socketPath)}`; probe: {probe}");
            probe.ShouldNotContain("allowed=open", customMessage: $"severed: the allowlisted IP is as unreachable as any other host, never reached unfiltered; probe: {probe}");
            probe.ShouldNotContain("denied=open", customMessage: $"and a host outside the allowlist is unreachable too; probe: {probe}");
            ProbeValue(probe, "worker").ShouldNotBe("open", $"and so is the worker's listener at the worker's own address; probe: {probe}");
            SealedEgressE2ETests.AssertSocketDirectoryIsReadOnly(ProbeValue(probe, "sock_unlink"), ProbeValue(probe, "sock_plant"), socketPath);

            output.WriteLine($"[durable-egress-e2e] ran {lane} allowlist-severed uid={NonRootWorker.EffectiveUid()} {probe} filter-unavailable=({FilteredEgressNetns.FilterUnavailableReason})");
        }
        finally
        {
            try { Directory.Delete(LocalProcessRunner.SpoolDirectoryFor(key), recursive: true); } catch { /* best-effort */ }
        }

        (await NetnsExistsAsync(NamespaceOf(key))).ShouldBeFalse("and no namespace was left behind for it");
    }

    /// <summary>An allowlist run of the probe below whose model is brokered, with the socket the executor mints for it on Linux, hardened as the executor hardens it, and told the worker's own address and <paramref name="workerListener"/>'s port.</summary>
    private static async Task<(SandboxSpec Spec, string SocketPath)> BrokeredAllowlistSpecAsync(LoopbackModelCredentialBroker broker, TcpListener workerListener)
    {
        var runId = Guid.NewGuid();
        var permissions = new AgentPermissions { Network = AgentNetworkAccess.On, Egress = AgentEgressPolicy.Allowlist };
        var socketPath = AgentRunExecutor.ModelBrokerSocketPathFor(permissions, runId).ShouldNotBeNull("the executor mints a socket for an allowlist run on Linux");
        var brokered = (await broker.OpenAsync(new() { RunId = runId, TeamId = Guid.NewGuid(), Epoch = 1, Upstream = new() { Provider = "Anthropic", ApiKey = "sk-allowlist-e2e" }, Ttl = TimeSpan.FromMinutes(5), SocketPath = socketPath }, CancellationToken.None)).ShouldNotBeNull();
        var spec = AgentRunExecutor.ApplyModelBrokerChannel(new SandboxSpec
        {
            Command = "/usr/bin/python3", Args = ["-c", AllowlistProbe], AllowNetwork = true, EgressAllowlist = [Allowed], TimeoutSeconds = 60,
            Environment = new Dictionary<string, string> { ["BROKER_URL"] = brokered.BaseUrl, ["RUN_TOKEN"] = brokered.RunToken, ["SOCK_PATH"] = socketPath, ["WORKER_IP"] = SealedEgressE2ETests.WorkerIpv4(), ["WORKER_PORT"] = ((IPEndPoint)workerListener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture) },
        }, brokered);

        spec.ModelBrokerSocketPath.ShouldBe(socketPath, "fixture check: the executor's own hardening stamps an allowlist run with its lease's socket");

        return (spec, socketPath);
    }

    /// <summary>The broker through the relay, the two writes the socket's read-only directory must refuse, the allowlisted IP and a denied one, the source address the run reaches the allowlisted IP from, and the worker's listener at the namespace's default gateway and at the worker's own address, from inside the run.</summary>
    private const string AllowlistProbe = SealedEgressE2ETests.SocketDirectoryWrites + "\n" + """
        import os, socket, urllib.request
        req = urllib.request.Request(os.environ['BROKER_URL'] + '/v1/messages', data=b'{}', method='POST', headers={'Authorization': 'Bearer ' + os.environ['RUN_TOKEN'], 'content-type': 'application/json'})
        try:
            broker = str(urllib.request.urlopen(req, timeout=10).status)
        except Exception as e:
            broker = type(e).__name__
        unlink, plant = sock_writes(os.environ['SOCK_PATH'])
        def tcp(host, port=80, timeout=6):
            try:
                socket.create_connection((host, port), timeout=timeout).close(); return 'open'
            except OSError as e:
                return type(e).__name__
        def gateway():
            for fields in (line.split() for line in open('/proc/net/route').read().splitlines()[1:]):
                if fields[1] == '00000000':
                    return socket.inet_ntoa(bytes.fromhex(fields[2])[::-1])
        def source(host):
            s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            try:
                s.connect((host, 80)); return s.getsockname()[0]
            except OSError as e:
                return type(e).__name__
            finally:
                s.close()
        port = int(os.environ['WORKER_PORT'])
        print('broker=%s sock_unlink=%s sock_plant=%s allowed=%s denied=%s src=%s gateway=%s worker=%s' % (broker, unlink, plant, tcp('1.1.1.1'), tcp('8.8.8.8'), source('1.1.1.1'), tcp(gateway(), port, 3), tcp(os.environ['WORKER_IP'], port, 3)))
        """;

    /// <summary>Whether <paramref name="address"/> lies in 198.19.64.0–198.19.191.255, the pool per-run /30s come from — by the test's own arithmetic, not the allocator's.</summary>
    private static bool IsInThePool(string address) =>
        System.Net.IPAddress.TryParse(address, out var ip) && ip.GetAddressBytes() is [198, 19, >= 64 and <= 191, _];

    /// <summary>The value the probe printed for <paramref name="key"/> (<c>key=value</c>), or <c>?</c> when it printed none.</summary>
    private static string ProbeValue(string probe, string key) =>
        probe.Split(' ').FirstOrDefault(pair => pair.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..] ?? "?";

    private sealed class OkUpstream : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") });
    }

    /// <summary>Launch a real durable run that curls <paramref name="target"/> with an allowlist of <paramref name="allow"/>, observe it to completion, and return the result. Also asserts the run was launched inside a netns keyed by the run.</summary>
    private static async Task<SandboxResult> DurableCurlAsync(LocalProcessRunner runner, string runKey, string allow, string target)
    {
        var spec = new SandboxSpec
        {
            Command = "curl",
            Args = new[] { "-s", "-m", "6", "-o", "/dev/null", $"http://{target}" },
            AllowNetwork = true,
            EgressAllowlist = new[] { allow },
            TimeoutSeconds = 40,
        };

        var handle = await runner.LaunchAsync(spec, runKey, CancellationToken.None);
        handle.EgressNetnsKey.ShouldBe(runKey, "an enforceable allowlist must launch the run inside a filtered netns keyed by the run, recorded on the handle for reap");

        return await runner.AttachAsync(handle, (_, _) => Task.CompletedTask, CancellationToken.None);
    }

    /// <summary>The netns name the durable launch derives for <paramref name="runKey"/> — reconstructed via the same pure plan builder the launch uses, so the assertion can't drift from the production name.</summary>
    private static string NamespaceOf(string runKey) => FilteredEgressPlan.NamespaceFor(runKey);

    /// <summary>True when <paramref name="ns"/> is still a live network namespace (parsing <c>ip netns list</c>), used to assert teardown actually removed it.</summary>
    private static async Task<bool> NetnsExistsAsync(string ns)
    {
        var psi = new ProcessStartInfo { FileName = "ip", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("netns");
        psi.ArgumentList.Add("list");

        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();

        // `ip netns list` prints "<name> (id: N)" per line — match the first token.
        return output.Split('\n').Any(line => line.Trim().Split(' ').FirstOrDefault() == ns);
    }
}
