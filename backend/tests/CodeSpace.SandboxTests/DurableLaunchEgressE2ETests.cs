using System.Diagnostics;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using Shouldly;

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
/// Uses raw IPs over plain HTTP so the signal is purely the egress filter — not DNS, not TLS.
/// </summary>
[Trait("Category", "Sandbox")]
public sealed class DurableLaunchEgressE2ETests
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
