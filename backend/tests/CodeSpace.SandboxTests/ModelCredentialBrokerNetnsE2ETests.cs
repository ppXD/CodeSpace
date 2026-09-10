using System.Diagnostics;
using CodeSpace.Core.Services.Agents.Credentials.Broker;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.SandboxTests;

/// <summary>
/// 🟢 Sandbox isolation E2E (high fidelity, Rule 12): the REAL model-credential broker reached by a REAL process
/// inside a REAL deny-by-default network namespace, over a live kernel. Needs ip + nft + CAP_NET_ADMIN, so it runs
/// for real ONLY in the privileged sandbox-isolation CI job; elsewhere
/// <see cref="FilteredEgressNetns.IsSupported"/> is false and it degrade-skips.
///
/// <para><b>The claim it settles.</b> A sealed run's whole point is that its egress allowlist is the only way out —
/// so "the broker is reachable from inside" cannot be argued from the code, it has to be observed. The allowlist here
/// is deliberately EMPTY: the namespace can reach nothing on the internet, and the broker still answers, because a
/// packet addressed to the host's own veth address is delivered locally (INPUT) rather than FORWARDED, and the plan's
/// filter is a forward hook. If that ever stops being true, every sealed brokered run loses its model and this test
/// is where it shows.</para>
///
/// <para>The second claim is the flip side: the bearer the sandbox holds is NOT the tenant's key. Sent straight to the
/// provider it buys nothing, so a token that escapes a run is not a credential.</para>
/// </summary>
[Trait("Category", "Sandbox")]
public sealed class ModelCredentialBrokerNetnsE2ETests
{
    private const string ProviderHost = "api.anthropic.com";

    [Fact]
    public async Task A_sealed_run_reaches_its_broker_and_is_refused_the_moment_the_lease_is_revoked()
    {
        if (!FilteredEgressNetns.IsSupported) return;   // no ip/nft (macOS dev / non-privileged) → the privileged CI job is authoritative

        using var broker = LoopbackModelCredentialBroker.ForTest(new AlwaysOkUpstream());
        var runId = Guid.NewGuid();

        var brokered = await broker.OpenAsync(
            new() { RunId = runId, TeamId = Guid.NewGuid(), Epoch = 1, Upstream = new() { Provider = "Anthropic", ApiKey = "sk-e2e-upstream-key" }, Ttl = TimeSpan.FromMinutes(5) },
            CancellationToken.None);

        brokered.ShouldNotBeNull("the broker must be able to listen on a host that can build filtered-egress namespaces — a sealed run has no other route to a model");

        // An EMPTY allowlist: this namespace can reach nothing on the internet. See the class remarks.
        var netnsKey = Guid.NewGuid().ToString("N");
        var setup = await FilteredEgressNetns.SetupAsync(netnsKey, Array.Empty<string>(), timeoutSeconds: 20, CancellationToken.None);

        try
        {
            setup.SetupOk.ShouldBeTrue($"the filtered netns must set up cleanly; setup error: {setup.SetupError}");
            setup.HostIp.ShouldNotBeNullOrWhiteSpace("the setup must report its gateway address — it is the only address a process inside the namespace can reach this worker at");

            // Resolve the broker's address through the PRODUCTION substitution the runner performs at launch, so the
            // URL the test curls is the one a real child would be handed.
            var url = ReachableUrl(brokered!, setup.HostIp!) + "/v1/messages";

            (await CurlInNetnsAsync(setup.ExecPrefix, url, brokered!.RunToken)).ShouldBe("200",
                customMessage: $"a sealed run must reach its broker at {setup.HostIp} with an EMPTY egress allowlist — if this is not 200, check by hand: `ip netns exec {FilteredEgressPlan.NamespaceFor(netnsKey)} curl -v {url}`. A host-destined packet is INPUT, not FORWARD, so the allowlist must not be involved");

            await broker.RevokeAsync(runId, "e2e-revoke", CancellationToken.None);

            (await CurlInNetnsAsync(setup.ExecPrefix, url, brokered.RunToken)).ShouldBe("401",
                customMessage: "after a revoke the sandbox's own bearer must be refused — a live process holding a withdrawn capability is exactly what this slice removes");
        }
        finally { await FilteredEgressNetns.TeardownAsync(netnsKey, CancellationToken.None); }
    }

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

    private static string ReachableUrl(BrokeredModelCredential brokered, string gatewayIp)
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
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") });
    }
}
