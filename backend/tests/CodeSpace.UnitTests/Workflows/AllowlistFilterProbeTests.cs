using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins what "this worker can filter an allowlist run" means (<see cref="FilteredEgressNetns.CanFilter"/>) and what the
/// launch derives from it (<see cref="LocalProcessRunner.FiltersAllowlist"/>): where bubblewrap confines, every step of
/// the allowlist setup must be able to run — the binaries, a namespace, forwarding — or the run is severed and relayed,
/// never planned into a namespace its setup would refuse after the run was admitted to spend; where nothing confines,
/// the binaries alone plan it, as they always did. Pure over the answers, so every row is pinned on any host; the real
/// kernel is the sandbox lanes' (<c>DurableLaunchEgressE2ETests</c>: filtered as root, severed as the non-root worker;
/// <c>UnconfinedWorkerE2ETests</c>: never on the worker's network where nothing confines).
/// </summary>
[Trait("Category", "Unit")]
public sealed class AllowlistFilterProbeTests : IDisposable
{
    private const string ToolsMissing = "ip or nft is not installed";

    private readonly string _dir = Directory.CreateTempSubdirectory("cs-filter-probe-").FullName;

    [Theory]
    [InlineData(true, true, true, null)]                    // every step can run: filtered
    [InlineData(true, true, false, "no forwarding")]        // a namespace, but nothing would be forwarded out of it
    [InlineData(true, false, true, "no namespace")]         // the binaries without the privilege to use them: the shipped non-root worker
    [InlineData(true, false, false, "no namespace")]        // the first wall the setup meets is the one named
    [InlineData(false, true, true, ToolsMissing)]           // no ip or nft
    [InlineData(false, true, false, ToolsMissing)]
    [InlineData(false, false, true, ToolsMissing)]
    [InlineData(false, false, false, ToolsMissing)]
    public void Where_bubblewrap_confines_an_allowlist_is_filtered_only_where_every_step_of_its_setup_can_run_and_severed_everywhere_else(bool tools, bool buildsNamespace, bool forwards, string? wall)
    {
        var namespaceAsked = false;

        var problem = FilteredEgressNetns.FilterProblem(tools, () => { namespaceAsked = true; return buildsNamespace ? null : "no namespace"; }, () => forwards ? null : "no forwarding");

        problem.ShouldBe(wall);
        namespaceAsked.ShouldBe(tools, "a host without ip is never asked to run it");

        var canFilter = problem is null;
        var filters = LocalProcessRunner.FiltersAllowlist(confines: true, canFilter, tools);
        var spec = new SandboxSpec { Command = "agent", AllowNetwork = true, EgressAllowlist = ["api.anthropic.com"], ModelBrokerPort = 43121, ModelBrokerSocketPath = "/spool/k/broker/seg/s" };

        filters.ShouldBe(canFilter, "where bubblewrap confines, the filter probe alone decides");
        LocalProcessRunner.EgressPolicyFor(spec, filters).Mode.ShouldBe(canFilter ? SandboxEgressMode.Filtered : SandboxEgressMode.None, "an allowlist that cannot be filtered FAILS CLOSED to no egress, never to the worker's network");

        // The setup builds a namespace, and so a prefix, for a Filtered run alone; what each posture then launches:
        var prefix = filters ? new[] { "ip", "netns", "exec", "cs-egr-deadbeef" } : Array.Empty<string>();
        var record = LocalProcessRunner.LaunchConfinement(spec, prefix, "/usr/bin/bwrap", unavailableReason: null);

        record.NetworkSevered.ShouldBe(!canFilter, "filtered in its namespace, or severed by bubblewrap — and the record says which");
        record.EgressSealedToBroker.ShouldBeFalse("sealed stays a network-off run's word: an allowlist run asked for network, and severing it is the fail-closed answer");
        LocalProcessRunner.RelaysModelBroker(spec, prefix.Length > 0, confines: true).ShouldBeTrue("filtered or severed, its network is its own, so it reaches its broker through the relay either way");
    }

    [Theory]
    [InlineData(true, true, true, true)]      // bubblewrap confines and every step can run: filtered
    [InlineData(true, false, true, false)]    // bubblewrap confines, the binaries without the privilege: severed and relayed — the shipped non-root worker
    [InlineData(true, false, false, false)]   // bubblewrap confines, no ip or nft: severed
    [InlineData(false, true, true, true)]     // nothing confines, every step can run: filtered, its namespace its only network
    [InlineData(false, false, true, true)]    // nothing confines and the setup would be refused: planned as it always was, so the setup aborts the launch — nothing here would enforce None
    [InlineData(false, false, false, false)]  // nothing confines, no ip or nft (macOS dev): as it always was
    public void Where_nothing_confines_the_binaries_alone_plan_an_allowlist_so_it_is_never_launched_on_the_worker_s_network(bool confines, bool canFilter, bool toolsPresent, bool filtered)
    {
        var spec = new SandboxSpec { Command = "agent", AllowNetwork = true, EgressAllowlist = ["api.anthropic.com"] };

        var filters = LocalProcessRunner.FiltersAllowlist(confines, canFilter, toolsPresent);

        filters.ShouldBe(filtered);

        // Planned Filtered, the launch runs in its namespace or aborts at its setup; otherwise bubblewrap severs it, or —
        // with no ip or nft to plan it and nothing to confine it, as before — it shares the worker's network.
        LocalProcessRunner.ChildNetworkIsPrivate(spec, filters, confines).ShouldBe(confines || toolsPresent, "a host with the binaries never launches an allowlist on the worker's network, confined or not");
    }

    [Theory]
    [InlineData("1", false, true)]      // already on: the setup's write, taken or ignored, changes nothing
    [InlineData("1", true, true)]
    [InlineData("0", true, true)]       // off, and this process may turn it on: the setup does
    [InlineData("0", false, false)]     // off, and the setup's write would be ignored (a read-only /proc/sys): nothing forwarded
    [InlineData(null, true, true)]      // unreadable, but the write takes
    [InlineData(null, false, false)]    // neither
    public void Forwarding_is_on_after_the_setup_where_it_already_reads_1_or_this_process_may_write_it(string? value, bool writable, bool forwards)
    {
        var problem = FilteredEgressNetns.ForwardingProblem(FilteredEgressNetns.IpForwardPath, value, writable);

        if (forwards) problem.ShouldBeNull();
        else problem.ShouldNotBeNull().ShouldStartWith(FilteredEgressNetns.IpForwardPath, customMessage: "the boot line names the file an operator has to make writable");
    }

    [Fact]
    public void A_real_file_that_already_reads_1_needs_no_write_even_one_this_process_may_not_make()
    {
        if (OperatingSystem.IsWindows()) return;

        var on = Path.Combine(_dir, "on");
        File.WriteAllText(on, "1\n");
        File.SetUnixFileMode(on, UnixFileMode.UserRead);

        // Root writes whatever the mode says, so there this row would pass through the writable branch instead: the pure
        // row ("1", false) pins the branch on every host, and a host that honours the mode pins it on a real file.
        if (OpensForWrite(on)) return;

        FilteredEgressNetns.ForwardingProblem(on).ShouldBeNull("a file that already reads 1 needs no write");
    }

    [Fact]
    public void The_forwarding_question_is_asked_of_the_real_file_and_changes_nothing()
    {
        if (OperatingSystem.IsWindows()) return;

        var off = Path.Combine(_dir, "off");
        File.WriteAllText(off, "0\n");

        FilteredEgressNetns.ForwardingProblem(off).ShouldBeNull("a file this process may write is one the setup's sysctl turns on");
        File.ReadAllText(off).ShouldBe("0\n", "asking changes nothing: the setup, not the probe, turns forwarding on");

        var absent = Path.Combine(_dir, "absent");

        FilteredEgressNetns.ForwardingProblem(absent).ShouldBe($"{absent} could not be read, and this process may not write it");
    }

    [Fact]
    public void The_forwarding_probe_reads_the_sysctl_the_setup_writes()
    {
        var plan = FilteredEgressPlan.Build("run-forwarding", ["1.1.1.1"], new EgressSubnetAllocator.Lease { Cidr = "198.19.70.16/30", HostIp = "198.19.70.17", NsIp = "198.19.70.18" }, []);

        var write = plan.SetupCommands.Single(argv => argv[0] == "sysctl");
        write.Take(2).ShouldBe(new[] { "sysctl", "-w" });

        var setting = write[2].Split('=');

        FilteredEgressNetns.IpForwardPath.ShouldBe("/proc/sys/" + setting[0].Replace('.', '/'), "the probe asks about the key the setup writes");
        setting[1].ShouldBe("1", "and the value it writes is the one the probe takes as already on");
    }

    private static bool OpensForWrite(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write);
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }
}
