using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using CodeSpace.Core.Services.Agents.Credentials.Broker;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using Shouldly;
using Xunit.Abstractions;

namespace CodeSpace.SandboxTests;

/// <summary>
/// 🟢 Sandbox isolation E2E (high fidelity, Rule 12): a network-off run whose model is brokered, launched by the REAL
/// <see cref="LocalProcessRunner"/> under bubblewrap, runs in a namespace SEALED to its broker — and a probe from inside
/// it, through the real chain (<c>ip netns exec</c> → prlimit → bwrap → python3), observes exactly one open door: the
/// real broker answers, while the internet, DNS over TCP and UDP, and another listener on the worker's own gateway
/// address all stay shut. The allowlist plan with no IPs would fail three of those (it accepts DNS anywhere, NATs out,
/// and has no input filter), and plain severing fails the first — which is why a network-off brokered run on a
/// confining host reached no model before this.
///
/// <para>Needs bwrap + ip + nft + the privilege to build a namespace, so it runs for real ONLY in the privileged
/// sandbox-isolation job; elsewhere it returns. Every arm that ran prints <see cref="RanMarker"/>, which the lane
/// requires, so a silent return can never pass for coverage.</para>
/// </summary>
[Trait("Category", "Sandbox")]
public sealed class SealedEgressE2ETests(ITestOutputHelper output) : IDisposable
{
    /// <summary>Printed by every arm that actually ran; the sandbox lane requires one per arm in the test output.</summary>
    public const string RanMarker = "[sealed-egress-e2e] ran";

    private readonly List<string> _spoolDirs = [];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_network_off_brokered_run_reaches_its_broker_and_nothing_else(bool durable)
    {
        if (!Seals()) return;

        using var broker = LoopbackModelCredentialBroker.ForTest(new AlwaysOkUpstream());
        var brokered = (await broker.OpenAsync(Lease(), CancellationToken.None)).ShouldNotBeNull("the broker must be able to listen on a host that seals — a sealed run has no other route to a model");

        using var otherListener = new TcpListener(IPAddress.Any, 0);
        otherListener.Start();

        var spec = new SandboxSpec
        {
            Command = "/usr/bin/python3",
            Args = ["-c", ProbeScript],
            AllowNetwork = false,
            ModelBrokerPort = brokered.RebindPort,
            // An egress proxy the worker (or the task) carries, as a proxied deployment would: the sealed namespace
            // cannot reach it, so the launch must drop it — which the probe reports directly, since the broker's NO_PROXY
            // exemption would let urllib past it even if the drop were gone.
            Environment = new Dictionary<string, string> { ["BROKER_URL"] = brokered.BaseUrl, ["RUN_TOKEN"] = brokered.RunToken, ["OTHER_PORT"] = ((IPEndPoint)otherListener.LocalEndpoint).Port.ToString(), ["HTTP_PROXY"] = "http://10.255.255.1:3128", ["http_proxy"] = "http://10.255.255.1:3128" },
            TimeoutSeconds = 60,
        };

        var (result, probe) = durable ? await RunDurableAsync(spec) : await RunAsync(spec);

        result.Status.ShouldBe(SandboxStatus.Success, $"the probe itself must run to its end inside the sealed namespace; stderr: {result.Stderr}");
        probe["broker"].ShouldBe("200", $"the run's own broker is the one destination a sealed run must reach — directly, not through the proxy it was handed; probe: {Describe(probe)}");
        probe["proxies"].ShouldBe("none", $"a sealed launch drops the proxy variables outright — the broker answering is not enough, since the NO_PROXY exemption alone lets urllib past a proxy a stricter reader would still use; probe: {Describe(probe)}");
        probe["internet"].ShouldNotBe("open", $"a sealed run must not reach the internet; probe: {Describe(probe)}");
        probe["dns_tcp"].ShouldNotBe("open", $"no DNS over TCP — a resolver is a tunnel; probe: {Describe(probe)}");
        probe["dns_udp"].ShouldNotBe("answered", $"no DNS over UDP either; probe: {Describe(probe)}");
        probe["gateway_other"].ShouldNotBe("open", $"another listener on the worker's own gateway address (its API, another run's broker) must stay shut; probe: {Describe(probe)}");

        output.WriteLine($"{RanMarker} {(durable ? "durable" : "non-durable")} {Describe(probe)}");
    }

    [Fact]
    public async Task A_sealed_namespace_drops_the_gateway_over_ipv6_link_local_too()
    {
        if (!Seals()) return;

        // Only the host knows its veth's link-local address, so this arm drives the namespace directly rather than
        // through a launch. A v4-only table would let this through; the sealed table is inet. Two things keep the arm
        // honest: it waits out duplicate-address detection (a tentative address refuses everything, table or not), and
        // it then deletes the table and connects again — the probe must get through without it, or its refusal proved
        // nothing about the table.
        var key = Guid.NewGuid().ToString("N");
        var setup = await FilteredEgressNetns.SetupSealedAsync(key, brokerPort: 9, timeoutSeconds: 20, CancellationToken.None);

        try
        {
            setup.SetupOk.ShouldBeTrue($"the sealed namespace must set up on this host: {setup.SetupError}");

            // The veth names are the run id's alone, so any lease names them the way the setup above did.
            var names = FilteredEgressPlan.BuildSealed(key, 9, new EgressSubnetAllocator.Lease { Cidr = "0.0.0.0/30", HostIp = "0.0.0.1", NsIp = "0.0.0.2" });

            if (await SettledLinkLocalAsync(["ip", "-6", "-o", "addr", "show", "dev", names.VethHost, "scope", "link"]) is not { } linkLocal)
            {
                output.WriteLine($"[sealed-egress-e2e] skipped ipv6 (IPv6 is disabled on this host, so there is no v6 path to close)");
                return;
            }

            (await SettledLinkLocalAsync(setup.ExecPrefix.Concat(["ip", "-6", "-o", "addr", "show", "dev", names.VethNs, "scope", "link"]).ToList())).ShouldNotBeNull("the namespace side's link-local must settle too, or it cannot send");

            using var listener = new TcpListener(IPAddress.IPv6Any, 0);
            listener.Start();

            var connect = $"import socket\ntry:\n s=socket.create_connection(('{linkLocal}%{names.VethNs}', {((IPEndPoint)listener.LocalEndpoint).Port}), timeout=3); s.close(); print('open')\nexcept OSError as e:\n print(type(e).__name__)";
            var probe = setup.ExecPrefix.Concat(["/usr/bin/python3", "-c", connect]).ToList();

            var sealedOutcome = (await RunHostAsync(probe)).Trim();
            (await RunHostExitAsync(["nft", "delete", "table", "inet", names.Namespace])).ShouldBe(0, "control setup: the sealed table must be there to delete");
            var controlOutcome = (await RunHostAsync(probe)).Trim();

            controlOutcome.ShouldBe("open", $"control: with the sealed table gone the same probe must reach the listener at {linkLocal}, or its refusal above proved nothing about the table (got {controlOutcome})");
            sealedOutcome.ShouldNotBe("open", $"the worker's listener must not be reachable over the host veth's IPv6 link-local address {linkLocal} while the sealed table stands");

            output.WriteLine($"{RanMarker} ipv6 outcome={sealedOutcome} control={controlOutcome}");
        }
        finally { await FilteredEgressNetns.TeardownAsync(key, CancellationToken.None); }
    }

    [Fact]
    public async Task A_30_still_held_by_a_run_that_outlived_its_worker_is_not_handed_to_the_next_run()
    {
        if (!Seals()) return;

        // A run survives its worker by design, and so do its namespace and veth; the reservation lock does not. A fresh
        // worker that trusted the lock alone would hand the survivor's /30 to its next sealed launch, and the kernel
        // would split the two runs' broker replies between two veths. Releasing the survivor's reservation without
        // tearing its namespace down is exactly what the restart leaves behind.
        var survivor = Guid.NewGuid().ToString("N");
        var next = Guid.NewGuid().ToString("N");
        var first = await FilteredEgressNetns.SetupSealedAsync(survivor, brokerPort: 9, timeoutSeconds: 20, CancellationToken.None);

        try
        {
            first.SetupOk.ShouldBeTrue($"the survivor's sealed namespace must set up on this host: {first.SetupError}");
            EgressSubnetAllocator.Host.Release(survivor);

            var second = await FilteredEgressNetns.SetupSealedAsync(next, brokerPort: 9, timeoutSeconds: 20, CancellationToken.None);

            try
            {
                second.SetupOk.ShouldBeTrue($"the next run's sealed namespace must set up: {second.SetupError}");
                second.HostIp.ShouldNotBe(first.HostIp, "the survivor's /30 is still on its veth; handing it out again routes one run's replies into the other's namespace");

                output.WriteLine($"{RanMarker} restart-reissue survivor={first.HostIp} next={second.HostIp}");
            }
            finally { await FilteredEgressNetns.TeardownAsync(next, CancellationToken.None); }
        }
        finally { await FilteredEgressNetns.TeardownAsync(survivor, CancellationToken.None); }
    }

    [Fact]
    public async Task A_host_whose_policy_rule_discards_the_run_s_replies_fails_the_setup_and_leaks_nothing()
    {
        if (!Seals()) return;

        // The allocator skips what the host's route listing covers, but a null route in a table that a policy rule
        // consults before main wins by rule ORDER, not prefix length: every step of the setup succeeds, and then every
        // reply from the broker to the namespace is discarded. The /30 here is from TEST-NET-1 (RFC 5737), which the
        // allocator never hands out, and the rule covers only that /30, so it cannot reach another run on this host.
        var third = RandomNumberGenerator.GetInt32(0, 64) * 4;
        var lease = new EgressSubnetAllocator.Lease { Cidr = $"192.0.2.{third}/30", HostIp = $"192.0.2.{third + 1}", NsIp = $"192.0.2.{third + 2}" };
        var table = RandomNumberGenerator.GetInt32(10_000, 1_000_000).ToString(CultureInfo.InvariantCulture);
        var rule = new[] { "pref", "100", "to", lease.Cidr, "lookup", table };
        var runId = Guid.NewGuid().ToString("N");
        var plan = FilteredEgressPlan.BuildSealed(runId, brokerPort: 9, lease);

        // A run of this test killed between its rule add and its cleanup leaves a rule for its /30 in a table this run
        // cannot name; left there, it would fail this run's control and blame the check.
        for (var stale = 0; stale < 8 && await RunHostExitAsync(["ip", "rule", "del", "pref", "100", "to", lease.Cidr]) == 0; stale++) { }

        try
        {
            var control = await FilteredEgressNetns.ApplyAsync(runId, plan, timeoutSeconds: 20, CancellationToken.None);
            control.SetupOk.ShouldBeTrue($"control: the same plan must set up on a host with no such rule, or the check refuses what it should admit: {control.SetupError}");
            await FilteredEgressNetns.TeardownAsync(runId, CancellationToken.None);

            (await RunHostExitAsync(["ip", "route", "add", "unreachable", "192.0.2.0/24", "table", table])).ShouldBe(0, "setup: the null route — broader than a /30, which the route listing ignores — must be installable in its own table");
            (await RunHostExitAsync(["ip", "rule", "add", .. rule])).ShouldBe(0, "setup: the rule that consults it before main must be installable");

            var refused = await FilteredEgressNetns.ApplyAsync(runId, plan, timeoutSeconds: 20, CancellationToken.None);

            refused.SetupOk.ShouldBeFalse("a namespace the host can never answer must fail its setup, not admit a run that spends its timeout on a dead broker");
            refused.SetupError.ShouldNotBeNull().ShouldContain(string.Join(' ', plan.RouteCheckArgv), customMessage: "the refusal names the lookup that found it, so an operator can rerun it");
            (await NetnsExistsAsync(plan.Namespace)).ShouldBeFalse("a setup that failed its route check tears its namespace down");
            (await RunHostExitAsync(["ip", "link", "show", plan.VethHost])).ShouldNotBe(0, "and the host end of its veth, with the address on it");

            output.WriteLine($"{RanMarker} policy-route-discard {refused.SetupError}");
        }
        finally
        {
            await RunHostExitAsync(["ip", "rule", "del", .. rule]);
            await RunHostExitAsync(["ip", "route", "flush", "table", table]);
            await FilteredEgressNetns.TeardownAsync(runId, CancellationToken.None);
        }
    }

    public void Dispose()
    {
        foreach (var dir in _spoolDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup of a spool dir */ }
        }
    }

    /// <summary>Probes every door from inside the run and prints one JSON object of what each did. It never fails on a shut door — the test decides.</summary>
    private const string ProbeScript = """
        import json, os, socket, urllib.parse, urllib.request
        res = {}
        url = os.environ['BROKER_URL']
        req = urllib.request.Request(url + '/v1/messages', data=b'{}', method='POST', headers={'Authorization': 'Bearer ' + os.environ['RUN_TOKEN'], 'content-type': 'application/json'})
        try:
            res['broker'] = str(urllib.request.urlopen(req, timeout=10).status)
        except Exception as e:
            res['broker'] = type(e).__name__ + ':' + str(e)
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
        res['gateway_other'] = tcp(urllib.parse.urlparse(url).hostname, int(os.environ['OTHER_PORT']))
        res['proxies'] = ','.join(sorted(n for n in os.environ if n.lower() in ('http_proxy', 'https_proxy', 'all_proxy'))) or 'none'
        print(json.dumps(res))
        """;

    /// <summary>A lane that seals is root with bwrap, ip and nft; there a namespace that cannot be built is a failure, not a skip.</summary>
    private static bool Seals()
    {
        if (BubblewrapSandbox.Available is null || !FilteredEgressNetns.IsSupported) return false;

        FilteredEgressNetns.CanSeal.ShouldBeTrue("bwrap, ip and nft are all here, but this process could not build a throwaway namespace — without that privilege every network-off brokered run is severed from its model");
        return true;
    }

    private async Task<(SandboxResult Result, Dictionary<string, string> Probe)> RunDurableAsync(SandboxSpec spec)
    {
        var key = Guid.NewGuid().ToString("N");
        var runner = new LocalProcessRunner();
        var lines = new List<string>();

        var handle = await runner.LaunchAsync(spec, key, CancellationToken.None);
        _spoolDirs.Add(handle.SpoolDirectory);

        handle.EgressNetnsKey.ShouldBe(key, "a sealed run must launch inside a namespace keyed by the run and recorded on its handle, or no reaper can tear it down");
        var confinement = handle.Confinement.ShouldNotBeNull("the launch must record what confinement it applied");
        confinement.EgressSealedToBroker.ShouldBeTrue("the record must say the run was sealed to its broker, not merely severed");
        confinement.NetworkSevered.ShouldBeTrue("a sealed run is severed from everything else");

        var result = await runner.AttachAsync(handle, (frame, _) => { lines.Add(frame.Text); return Task.CompletedTask; }, CancellationToken.None);

        (await NetnsExistsAsync(FilteredEgressPlan.NamespaceFor(key))).ShouldBeFalse("the sealed namespace must be torn down on the run's terminal path");
        (await RunHostExitAsync(["nft", "list", "table", "inet", FilteredEgressPlan.NamespaceFor(key)])).ShouldNotBe(0, "and so must its host-side inet table, which deleting the namespace does not remove");

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

        return line is null ? new Dictionary<string, string> { ["broker"] = $"no probe output: {stdout}", ["internet"] = "?", ["dns_tcp"] = "?", ["dns_udp"] = "?", ["gateway_other"] = "?" } : JsonSerializer.Deserialize<Dictionary<string, string>>(line)!;
    }

    private static string Describe(Dictionary<string, string> probe) => string.Join(' ', probe.Select(p => $"{p.Key}={p.Value}"));

    private static ModelCredentialLeaseRequest Lease() =>
        new() { RunId = Guid.NewGuid(), TeamId = Guid.NewGuid(), Epoch = 1, Upstream = new() { Provider = "Anthropic", ApiKey = "sk-sealed-e2e-upstream" }, Ttl = TimeSpan.FromMinutes(5) };

    /// <summary>The link-local address <paramref name="listArgv"/> reports once duplicate-address detection has finished with it (no longer "tentative"), or null where IPv6 is disabled and there is none. Fails if it never settles.</summary>
    private static async Task<string?> SettledLinkLocalAsync(IReadOnlyList<string> listArgv)
    {
        var watch = Stopwatch.StartNew();

        while (true)
        {
            var line = (await RunHostAsync(listArgv)).Split('\n').FirstOrDefault(l => l.Contains(" fe80:", StringComparison.OrdinalIgnoreCase));

            if (line is null && watch.Elapsed >= TimeSpan.FromSeconds(3)) return null;   // no link-local at all: IPv6 is off here

            if (line is not null && !line.Contains("tentative", StringComparison.Ordinal))
                return line.Split(' ', StringSplitOptions.RemoveEmptyEntries).First(t => t.StartsWith("fe80:", StringComparison.OrdinalIgnoreCase)).Split('/')[0];

            watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(15), $"the link-local address never left duplicate-address detection: {line?.Trim()}");
            await Task.Delay(200);
        }
    }

    private static async Task<int> RunHostExitAsync(IReadOnlyList<string> argv)
    {
        var psi = new ProcessStartInfo { FileName = argv[0], UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in argv.Skip(1)) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        await process.WaitForExitAsync();

        return process.ExitCode;
    }

    private static async Task<bool> NetnsExistsAsync(string ns) =>
        (await RunHostAsync(["ip", "netns", "list"])).Split('\n').Any(line => line.Trim().Split(' ').FirstOrDefault() == ns);

    private static async Task<string> RunHostAsync(IReadOnlyList<string> argv)
    {
        var psi = new ProcessStartInfo { FileName = argv[0], UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in argv.Skip(1)) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        return stdout;
    }

    /// <summary>The provider, answering 200 to anything the broker relays — this lane asserts reachability, never what a model said.</summary>
    private sealed class AlwaysOkUpstream : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") });
    }
}
