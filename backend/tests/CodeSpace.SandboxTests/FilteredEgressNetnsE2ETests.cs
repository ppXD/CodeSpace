using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using Shouldly;

namespace CodeSpace.SandboxTests;

/// <summary>
/// 🟢 Sandbox isolation E2E (high fidelity, Rule 12): the REAL deny-by-default egress allowlist (B3.2) against a live
/// kernel — sets up a filtered network namespace whose only egress is the allowlisted IP, runs a real <c>curl</c>
/// inside it, and proves an ALLOWED host is reachable while a NON-allowed host is dropped. Needs ip + nft +
/// CAP_NET_ADMIN, so it runs for real ONLY in the privileged sandbox-isolation CI job (which installs iproute2 +
/// nftables); elsewhere (no nft / not privileged) <see cref="FilteredEgressNetns.IsSupported"/> is false and it
/// degrade-skips. Uses raw IPs (no DNS) so the assertion is purely the egress filter, not name resolution.
///
/// <para>Class-level <c>[Trait("Category", "Sandbox")]</c> — runs in the same privileged gate as the bwrap
/// confinement tests. The teardown is the executor's own best-effort netns/table cleanup (no leak between runs).</para>
/// </summary>
[Trait("Category", "Sandbox")]
public sealed class FilteredEgressNetnsE2ETests
{
    // Cloudflare 1.1.1.1 + Google 8.8.8.8 both serve HTTPS on the open internet — so if the filter did NOT enforce,
    // BOTH would be reachable. The allowlist permits ONLY 1.1.1.1, so 8.8.8.8 being unreachable proves the drop.
    private const string Allowed = "1.1.1.1";
    private const string Denied = "8.8.8.8";

    [Fact]
    public async Task An_allowed_host_is_reachable_and_a_non_allowed_host_is_dropped()
    {
        if (!FilteredEgressNetns.IsSupported) return;   // no ip/nft (macOS dev / non-privileged) → the privileged CI job is authoritative

        var reachAllowed = await CurlInFilteredNetnsAsync(allow: Allowed, target: Allowed);
        reachAllowed.SetupOk.ShouldBeTrue($"the filtered netns must set up cleanly; setup error: {reachAllowed.SetupError}");
        reachAllowed.ExitCode.ShouldBe(0, $"the ALLOWED host {Allowed} must be reachable through the egress allowlist. Output: {reachAllowed.Output}");

        var reachDenied = await CurlInFilteredNetnsAsync(allow: Allowed, target: Denied);
        reachDenied.SetupOk.ShouldBeTrue($"the filtered netns must set up cleanly; setup error: {reachDenied.SetupError}");
        reachDenied.ExitCode.ShouldNotBe(0, $"a NON-allowed host ({Denied}) must be DROPPED — the deny-by-default egress filter is the whole point. If this reaches it, the filter is not enforcing. Output: {reachDenied.Output}");
    }

    [Fact]
    public async Task The_durable_setup_teardown_split_enforces_the_filter_and_teardown_is_reconstructable_from_runId()
    {
        // The contract the DURABLE launch (B3.2b) relies on: SetupAsync builds the netns + returns the ExecPrefix the
        // detached process runs behind, and TeardownAsync — reconstructed PURELY from the runId — tears it down at reap
        // (possibly on a different worker after a crash). Proves the split is usable + leak-free, not just RunAsync.
        if (!FilteredEgressNetns.IsSupported) return;

        var runId = Guid.NewGuid().ToString("N");
        var setup = await FilteredEgressNetns.SetupAsync(runId, new[] { Allowed }, timeoutSeconds: 20, CancellationToken.None);

        try
        {
            setup.SetupOk.ShouldBeTrue($"setup must succeed; error: {setup.SetupError}");
            setup.ExecPrefix.ShouldNotBeEmpty("the ExecPrefix is what the durable launch prepends to run its process inside the netns");

            // Run curl INSIDE the netns via the ExecPrefix — exactly how the durable launch will prefix its command chain.
            (await RunViaPrefixAsync(setup.ExecPrefix, Allowed)).ShouldBe(0, "the ALLOWED host is reachable through the set-up netns");
            (await RunViaPrefixAsync(setup.ExecPrefix, Denied)).ShouldNotBe(0, "the DENIED host is dropped — SetupAsync's netns enforces the filter");
        }
        finally
        {
            await FilteredEgressNetns.TeardownAsync(runId, CancellationToken.None);   // reconstructed from runId alone — the reap/crash-resume contract
        }

        // Teardown actually freed the runId-derived names: a second SetupAsync with the SAME runId succeeds (it would
        // collide on the still-present ns/table otherwise). This is the leak-free guarantee.
        var resetup = await FilteredEgressNetns.SetupAsync(runId, new[] { Allowed }, timeoutSeconds: 20, CancellationToken.None);
        resetup.SetupOk.ShouldBeTrue("teardown freed the runId-derived ns/table so a re-setup succeeds — proving teardown cleaned up, no leak");
        await FilteredEgressNetns.TeardownAsync(runId, CancellationToken.None);
    }

    private static Task<FilteredEgressNetns.Outcome> CurlInFilteredNetnsAsync(string allow, string target) =>
        FilteredEgressNetns.RunAsync(
            runId: Guid.NewGuid().ToString("N"),
            allowedIps: new[] { allow },
            command: "curl",
            args: new[] { "-s", "-m", "6", "-o", "/dev/null", $"https://{target}" },
            timeoutSeconds: 40,
            cancellationToken: CancellationToken.None);

    /// <summary>Run curl against <paramref name="target"/> behind the netns ExecPrefix (the durable-launch shape) and return its exit code.</summary>
    private static async Task<int> RunViaPrefixAsync(IReadOnlyList<string> execPrefix, string target)
    {
        var argv = execPrefix.Concat(new[] { "curl", "-s", "-m", "6", "-o", "/dev/null", $"https://{target}" }).ToList();

        var psi = new System.Diagnostics.ProcessStartInfo { FileName = argv[0], UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in argv.Skip(1)) psi.ArgumentList.Add(a);

        using var p = System.Diagnostics.Process.Start(psi)!;
        await p.WaitForExitAsync();
        return p.ExitCode;
    }
}
