using System.Globalization;
using System.Security.Cryptography;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using Shouldly;
using Xunit.Abstractions;

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
///
/// <para>The two arms about the host's own routing (a /30 still held by a namespace that outlived its worker, a policy
/// rule that discards the run's replies) print <see cref="RanMarker"/>, which the lane requires.</para>
/// </summary>
[Trait("Category", "Sandbox")]
public sealed class FilteredEgressNetnsE2ETests(ITestOutputHelper output)
{
    /// <summary>Printed by the arms that must not pass by returning early; the sandbox lane requires one per arm.</summary>
    public const string RanMarker = "[filtered-egress-e2e] ran";

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

    [Fact]
    public async Task A_30_still_held_by_a_run_that_outlived_its_worker_is_not_handed_to_the_next_run()
    {
        if (!BuildsNamespaces()) return;

        // A run survives its worker by design, and so do its namespace and veth; the reservation lock does not. A fresh
        // worker that trusted the lock alone would hand the survivor's /30 to its next allowlist launch, and the kernel
        // would split the two runs' replies between two veths. Releasing the survivor's reservation without tearing its
        // namespace down is exactly what the restart leaves behind.
        var survivor = Guid.NewGuid().ToString("N");
        var next = Guid.NewGuid().ToString("N");
        var first = await FilteredEgressNetns.SetupAsync(survivor, new[] { Allowed }, timeoutSeconds: 20, CancellationToken.None);

        try
        {
            first.SetupOk.ShouldBeTrue($"the survivor's allowlist namespace must set up on this host: {first.SetupError}");
            EgressSubnetAllocator.Host.Release(survivor);

            var second = await FilteredEgressNetns.SetupAsync(next, new[] { Allowed }, timeoutSeconds: 20, CancellationToken.None);

            try
            {
                second.SetupOk.ShouldBeTrue($"the next run's allowlist namespace must set up: {second.SetupError}");
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
        if (!BuildsNamespaces()) return;

        // The allocator skips what the host's route listing covers, but a null route in a table that a policy rule
        // consults before main wins by rule ORDER, not prefix length: every step of the setup succeeds, and then every
        // reply to the namespace is discarded. The /30 here is from TEST-NET-1 (RFC 5737), which the allocator never
        // hands out, and the rule covers only that /30, so it cannot reach another run on this host.
        var third = RandomNumberGenerator.GetInt32(0, 64) * 4;
        var lease = new EgressSubnetAllocator.Lease { Cidr = $"192.0.2.{third}/30", HostIp = $"192.0.2.{third + 1}", NsIp = $"192.0.2.{third + 2}" };
        var table = RandomNumberGenerator.GetInt32(10_000, 1_000_000).ToString(CultureInfo.InvariantCulture);
        string[] rule = ["pref", "100", "to", lease.Cidr, "lookup", table];
        var runId = Guid.NewGuid().ToString("N");
        var plan = FilteredEgressPlan.Build(runId, new[] { Allowed }, lease);

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

            refused.SetupOk.ShouldBeFalse("a namespace the host can never answer must fail its setup, not admit a run that spends its timeout unanswered");
            refused.SetupError.ShouldNotBeNull().ShouldContain(string.Join(' ', plan.RouteCheckArgv), customMessage: "the refusal names the lookup that found it, so an operator can rerun it");
            (await RunHostAsync(["ip", "netns", "list"])).Split('\n').ShouldNotContain(line => line.Trim().Split(' ')[0] == plan.Namespace, "a setup that failed its route check tears its namespace down");
            (await RunHostExitAsync(["ip", "link", "show", plan.VethHost])).ShouldNotBe(0, "and the host end of its veth, with the address on it");

            output.WriteLine($"{RanMarker} policy-route-discard-dst {refused.SetupError}");
        }
        finally
        {
            await RunHostExitAsync(["ip", "rule", "del", .. rule]);
            await RunHostExitAsync(["ip", "route", "flush", "table", table]);
            await FilteredEgressNetns.TeardownAsync(runId, CancellationToken.None);
        }
    }

    /// <summary>A lane with ip and nft builds namespaces (the root lane); there, one that cannot be built is a failure, not a skip.</summary>
    private static bool BuildsNamespaces()
    {
        if (!FilteredEgressNetns.IsSupported) return false;

        FilteredEgressNetns.CanSeal.ShouldBeTrue("ip and nft are here, but this process could not build a throwaway namespace — an allowlist run could not be filtered on this host");
        return true;
    }

    private static async Task<int> RunHostExitAsync(IReadOnlyList<string> argv)
    {
        var psi = new System.Diagnostics.ProcessStartInfo { FileName = argv[0], UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in argv.Skip(1)) psi.ArgumentList.Add(arg);

        using var process = System.Diagnostics.Process.Start(psi)!;
        await process.WaitForExitAsync();

        return process.ExitCode;
    }

    private static async Task<string> RunHostAsync(IReadOnlyList<string> argv)
    {
        var psi = new System.Diagnostics.ProcessStartInfo { FileName = argv[0], UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in argv.Skip(1)) psi.ArgumentList.Add(arg);

        using var process = System.Diagnostics.Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        return stdout;
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
