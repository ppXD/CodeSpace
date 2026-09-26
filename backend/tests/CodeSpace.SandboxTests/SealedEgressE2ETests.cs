using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
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
            Environment = new Dictionary<string, string> { ["BROKER_URL"] = brokered.BaseUrl, ["RUN_TOKEN"] = brokered.RunToken, ["OTHER_PORT"] = ((IPEndPoint)otherListener.LocalEndpoint).Port.ToString() },
            TimeoutSeconds = 60,
        };

        var (result, probe) = durable ? await RunDurableAsync(spec) : await RunAsync(spec);

        result.Status.ShouldBe(SandboxStatus.Success, $"the probe itself must run to its end inside the sealed namespace; stderr: {result.Stderr}");
        probe["broker"].ShouldBe("200", $"the run's own broker is the one destination a sealed run must reach; probe: {Describe(probe)}");
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
        // through a launch. A v4-only table would let this through; the sealed table is inet.
        var key = Guid.NewGuid().ToString("N");
        var setup = await FilteredEgressNetns.SetupSealedAsync(key, brokerPort: 9, timeoutSeconds: 20, CancellationToken.None);

        try
        {
            setup.SetupOk.ShouldBeTrue($"the sealed namespace must set up on this host: {setup.SetupError}");

            // The veth names are the run id's alone, so any lease names them the way the setup above did.
            var names = FilteredEgressPlan.BuildSealed(key, 9, new EgressSubnetAllocator.Lease { Cidr = "0.0.0.0/30", HostIp = "0.0.0.1", NsIp = "0.0.0.2" });
            var linkLocal = await HostLinkLocalAsync(names.VethHost);

            if (linkLocal is null)
            {
                output.WriteLine($"{RanMarker} ipv6 skipped=no-link-local (IPv6 disabled on this host, so there is no v6 path to close)");
                return;
            }

            using var listener = new TcpListener(IPAddress.IPv6Any, 0);
            listener.Start();

            var connect = $"import socket\ntry:\n s=socket.create_connection(('{linkLocal}%{names.VethNs}', {((IPEndPoint)listener.LocalEndpoint).Port}), timeout=3); s.close(); print('open')\nexcept OSError as e:\n print(type(e).__name__)";
            var outcome = (await RunHostAsync(setup.ExecPrefix.Concat(["/usr/bin/python3", "-c", connect]).ToList())).Trim();

            outcome.ShouldNotBe("open", $"the worker's listener must not be reachable over the host veth's IPv6 link-local address {linkLocal}; check by hand: `{string.Join(' ', setup.ExecPrefix)} python3 -c ...`");

            output.WriteLine($"{RanMarker} ipv6 outcome={outcome}");
        }
        finally { await FilteredEgressNetns.TeardownAsync(key, CancellationToken.None); }
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

    /// <summary>The host veth's IPv6 link-local address, or null where IPv6 is disabled and the veth has none.</summary>
    private static async Task<string?> HostLinkLocalAsync(string hostVeth)
    {
        var text = await RunHostAsync(["ip", "-6", "-o", "addr", "show", "dev", hostVeth, "scope", "link"]);
        var token = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(t => t.StartsWith("fe80:", StringComparison.OrdinalIgnoreCase));

        return token?.Split('/')[0];
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
