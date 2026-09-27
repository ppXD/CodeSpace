using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Mcp.Relay;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins how the local runner puts a brokered CLI whose network is its own behind the <c>codespace-mcp relay</c>: the
/// one predicate that decides it (<see cref="LocalProcessRunner.RelaysModelBroker"/>), the order of the chain it builds
/// (<c>cgroup → netns → prlimit → bwrap → relay → cli</c>), what bubblewrap binds for it, the confinement record, and
/// that every launch the relay is not for keeps exactly the argv it had. Pure over the host's two tool paths
/// (<see cref="LocalProcessRunner.ChildCommand"/>), so every posture is pinned on any host; the real kernel is the
/// sandbox lane's (<c>SealedEgressE2ETests</c>, <c>NonRootWorkerE2ETests</c>).
/// </summary>
[Trait("Category", "Unit")]
public sealed class ModelBrokerRelayRunnerTests
{
    private const string Bwrap = "/usr/bin/bwrap";
    private const string Prlimit = "/usr/bin/prlimit";
    private const int Port = 43121;
    private const string Socket = "/spool/k/broker/seg/s";

    // ── The relay's argv, and the helper that reads it ──────────────────────────────────────────────────────────

    [Fact]
    public void The_relay_wrap_is_the_helper_s_own_verb_and_the_helper_parses_exactly_what_it_builds()
    {
        // A cross-process seam with no compiler across it: the runner writes this argv, the helper parses it. So the
        // assertion goes through the helper's REAL parser, not through the writer's output alone.
        var (command, args) = ModelBrokerRelay.Wrap("/app/codespace-mcp", Port, Socket, "claude", ["-p", "--model", "m"]);

        command.ShouldBe("/app/codespace-mcp");
        args.ShouldBe(new[] { "relay", "43121", Socket, "--", "claude", "-p", "--model", "m" });
        ModelBrokerRelay.Verb.ShouldBe(BrokerRelay.Verb, "the runner's verb is the one the helper dispatches on");

        var parsed = BrokerRelayCommand.Parse(args.Skip(1).ToList());

        parsed.Port.ShouldBe(Port);
        parsed.Broker.ToString().ShouldBe(Socket);
        parsed.Cli.ShouldBe("claude");
        parsed.CliArgs.ShouldBe(new[] { "-p", "--model", "m" });
    }

    [Fact]
    public void The_admission_reads_back_the_helper_s_own_listen_failed_status() =>
        // The admission asks the helper to listen on a port this process holds and takes this status as "it runs the
        // relay" (ModelBrokerRelay.HelperRunsRelay). The helper's constant owns the value; the two ends must agree.
        ModelBrokerRelay.ListenFailedExitCode.ShouldBe(BrokerRelay.ListenFailedExitCode);

    // ── The one predicate ──────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, true, false, false, false, true, true)]      // network off, confined: bwrap severs it — relayed
    [InlineData(true, true, false, false, false, false, false)]    // network off on a host that does not confine: it shares the worker's network and calls loopback itself
    [InlineData(true, false, false, false, false, true, false)]    // no socket on the lease: nothing to relay to (the admission refuses it)
    [InlineData(false, true, false, false, false, true, false)]    // no broker port: nothing brokered
    [InlineData(true, true, true, false, false, true, false)]      // network on, no allowlist: the worker's own network
    [InlineData(true, true, true, true, true, true, true)]         // an allowlist in its filtered namespace, under bwrap
    [InlineData(true, true, true, true, true, false, true)]        // an allowlist in its namespace on a host with no bwrap: relayed without it
    [InlineData(true, true, true, true, false, true, true)]        // an allowlist this host cannot enforce: bwrap severs it, and it is relayed
    [InlineData(true, true, true, true, false, false, false)]      // …and unconfined, it shares the worker's network
    public void A_child_is_relayed_exactly_when_its_network_is_its_own_and_its_lease_has_a_socket(bool port, bool socket, bool allowNetwork, bool allowlist, bool inNamespace, bool confines, bool relays)
    {
        var spec = new SandboxSpec { Command = "agent", AllowNetwork = allowNetwork, EgressAllowlist = allowlist ? ["api.anthropic.com"] : null, ModelBrokerPort = port ? Port : null, ModelBrokerSocketPath = socket ? Socket : null };

        LocalProcessRunner.RelaysModelBroker(spec, inNamespace, confines).ShouldBe(relays);
    }

    // ── The chain, in order ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_network_off_relayed_chain_is_cgroup_then_prlimit_then_bwrap_then_the_relay_then_the_cli()
    {
        var spec = RelayedSpec(allowNetwork: false) with { Command = "/usr/bin/claude", Args = ["-p", "go"], MaxProcesses = 64, MaxFileSizeMb = 1 };

        var argv = Chain(spec, egressPrefix: [], bwrap: Bwrap);

        argv.Take(2).ShouldBe(new[] { "CGSELF", "x" }, "the cgroup self-add is outermost");
        argv[2].ShouldBe(Prlimit, "no namespace of the worker's for a network-off run: prlimit comes next");
        At(argv, Bwrap).ShouldBeGreaterThan(At(argv, Prlimit), "bubblewrap inside prlimit");
        argv.ShouldContain("--unshare-net", "the network is off: bubblewrap severs it, and the relay is the one way out");

        var relay = At(argv, LocalProcessRunner.McpProxyBinaryPath());

        relay.ShouldBeGreaterThan(At(argv, Bwrap), "the relay runs INSIDE bubblewrap, so it binds the sandbox's loopback and not the worker's");
        argv[relay - 1].ShouldBe("--", "the relay is bubblewrap's command");
        argv.Skip(relay).ShouldBe(new[] { LocalProcessRunner.McpProxyBinaryPath(), "relay", Port.ToString(), Socket, "--", "/usr/bin/claude", "-p", "go" }, "and the CLI, with its own argv, is the relay's");
    }

    [Fact]
    public void An_allowlist_relayed_chain_enters_its_namespace_before_prlimit_and_shares_it_inside_bwrap()
    {
        var spec = RelayedSpec(allowNetwork: true) with { EgressAllowlist = ["api.anthropic.com"], Command = "/usr/bin/codex", Args = ["exec"] };

        var argv = Chain(spec, egressPrefix: ["EGRESS", "y"], bwrap: Bwrap);

        argv.Take(4).ShouldBe(new[] { "CGSELF", "x", "EGRESS", "y" }, "cgroup, then the filtered namespace, outermost");
        argv[4].ShouldBe(Prlimit);
        argv.ShouldNotContain("--unshare-net", "inside its filtered namespace bubblewrap shares it, so the allowlist still holds");
        argv.TakeLast(7).ShouldBe(new[] { LocalProcessRunner.McpProxyBinaryPath(), "relay", Port.ToString(), Socket, "--", "/usr/bin/codex", "exec" }, "the relay is innermost, in front of the CLI");
    }

    [Fact]
    public void In_a_namespace_on_a_host_with_no_bwrap_the_relay_is_applied_directly()
    {
        var spec = RelayedSpec(allowNetwork: true) with { EgressAllowlist = ["api.anthropic.com"], Command = "codex", Args = ["exec"] };

        var argv = Chain(spec, egressPrefix: ["EGRESS", "y"], bwrap: null, prlimit: null);

        argv.ShouldBe(new[] { "CGSELF", "x", "EGRESS", "y", LocalProcessRunner.McpProxyBinaryPath(), "relay", Port.ToString(), Socket, "--", "codex", "exec" }, "the P6 posture: a namespace with no bubblewrap still needs the relay, and nothing stands its CLI's own sandbox down");
    }

    [Fact]
    public void A_cli_s_own_sandbox_is_stood_down_on_its_own_argv_inside_the_relay()
    {
        // The stand-down swaps the CLI's argv, so it must land on the CLI's, not the relay's: applied after the relay
        // wrapped the argv, the fragment would not be found, and the launch would refuse.
        var substitution = new ArgsSubstitution { Replace = ["--sandbox", "read-only"], With = ["--sandbox", CodexHarness.ConfinedSandboxMode] };
        var spec = RelayedSpec(allowNetwork: false) with { Command = "codex", Args = ["exec", "--sandbox", "read-only", "-"], WhenRunnerConfines = substitution };

        var argv = Chain(spec, egressPrefix: [], bwrap: Bwrap, prlimit: null);

        argv.TakeLast(5).ShouldBe(new[] { "codex", "exec", "--sandbox", CodexHarness.ConfinedSandboxMode, "-" }, "the CLI runs stood down behind the relay");
        argv.ShouldNotContain("read-only");
    }

    [Fact]
    public void A_durable_launch_on_this_host_relays_exactly_where_bwrap_confines_it()
    {
        // Honest on either host: the durable builder is the same chain, fed this host's own tool paths.
        var spec = RelayedSpec(allowNetwork: false) with { Command = "agent", Args = ["go"] };

        var argv = LocalProcessRunner.BuildDurableStartInfo(spec, Path.Combine(Path.GetTempPath(), "cs-relay-" + Guid.NewGuid().ToString("N"))).ArgumentList;

        argv.Contains("relay").ShouldBe(BubblewrapSandbox.Available is not null, "a network-off brokered child is relayed wherever bubblewrap severs it, and nowhere else");
        argv.TakeLast(2).ShouldBe(new[] { "agent", "go" }, "the CLI and its argv still trail the chain");
    }

    // ── Every other launch keeps its argv ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, Bwrap)]    // network on, no allowlist: the child shares the worker's network and calls its lease directly
    [InlineData(false, null)]    // network off on a host that does not confine (macOS dev, a pod without userns): unconfined, nothing to relay
    public void A_lease_with_a_socket_changes_nothing_for_a_child_that_shares_the_worker_s_network(bool allowNetwork, string? bwrap)
    {
        var relayedLease = RelayedSpec(allowNetwork) with { Command = "/usr/bin/claude", Args = ["-p", "go"] };
        var noLease = relayedLease with { ModelBrokerPort = null, ModelBrokerSocketPath = null };

        Chain(relayedLease, egressPrefix: [], bwrap).ShouldBe(Chain(noLease, egressPrefix: [], bwrap), "byte for byte: the broker's socket is for children that cannot reach loopback, and this one can");
    }

    // ── What bubblewrap binds for it ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_relayed_plan_binds_the_socket_directory_and_the_helper_read_only_and_the_cli_s_own_directory()
    {
        var spec = RelayedSpec(allowNetwork: false) with { Command = "/srv/cs-cli/bin/claude", WorkingDirectory = "/work/ws" };
        var (relay, relayArgs) = ModelBrokerRelay.Wrap(LocalProcessRunner.McpProxyBinaryPath(), Port, Socket, spec.Command, spec.Args);

        var plan = LocalProcessRunner.PlanFor(spec, relay, relayArgs, "/spool/k/agent-home", Array.Empty<string>());
        var argv = BubblewrapSandbox.BuildArgs(plan).ToList();

        plan.Command.ShouldBe(relay, "the relay is what bubblewrap runs");
        BoundAs(argv, "/spool/k/broker/seg").ShouldBe("--ro-bind-try", "the socket's own directory, read-only: connect() needs no write, and a read-only bind stops the agent unlinking or planting a socket");
        plan.WritablePaths.ShouldNotContain("/spool/k/broker/seg");
        foreach (var file in LocalProcessRunner.McpProxyFiles(relay)) BoundAs(argv, file).ShouldBe("--ro-bind-try", $"the helper that runs the relay is bound file by file ({file})");
        BoundAs(argv, Path.GetDirectoryName(relay)!).ShouldBeEmpty("never the helper's directory, the worker's own app dir");
        BoundAs(argv, "/srv/cs-cli/bin").ShouldBe("--ro-bind-try", "the CLI is no longer bubblewrap's command, so its own directory is bound for it, as it was when it was");
    }

    [Fact]
    public void A_relayed_cli_under_the_read_only_roots_needs_no_bind_of_its_own()
    {
        var spec = RelayedSpec(allowNetwork: false) with { Command = "/usr/bin/claude" };
        var (relay, relayArgs) = ModelBrokerRelay.Wrap(LocalProcessRunner.McpProxyBinaryPath(), Port, Socket, spec.Command, spec.Args);

        var plan = LocalProcessRunner.PlanFor(spec, relay, relayArgs, null, Array.Empty<string>());

        plan.ReadOnlyExtraPaths.ShouldNotContain("/usr/bin", "/usr is already a read-only root");
    }

    [Fact]
    public void A_plan_that_is_not_relayed_binds_nothing_new()
    {
        var spec = new SandboxSpec { Command = "/srv/cs-cli/bin/claude", AllowNetwork = true, ModelBrokerPort = Port, ModelBrokerSocketPath = Socket };

        var plan = LocalProcessRunner.PlanFor(spec, spec.Command, spec.Args, null, Array.Empty<string>());

        plan.ReadOnlyExtraPaths.ShouldBeEmpty("a child on the worker's own network reaches its lease on loopback: no socket, no helper, no extra directory");
        plan.Command.ShouldBe(spec.Command);
    }

    // ── The record ─────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false, true, false, true, true)]    // network off, relayed: severed, and sealed to its broker
    [InlineData(false, false, false, true, false)]  // network off, no socket: severed, and nothing reached (the admission refuses it)
    [InlineData(true, true, true, false, false)]    // an allowlist relayed in its namespace: filtered, not severed, not sealed
    public void A_confined_launch_records_the_route_its_relay_kept(bool allowNetwork, bool socket, bool inNamespace, bool expectedSevered, bool expectedSealed)
    {
        var spec = new SandboxSpec { Command = "agent", AllowNetwork = allowNetwork, EgressAllowlist = inNamespace ? ["api.anthropic.com"] : null, ModelBrokerPort = Port, ModelBrokerSocketPath = socket ? Socket : null };

        var record = LocalProcessRunner.LaunchConfinement(spec, inNamespace ? ["ip", "netns", "exec", "cs-egr-deadbeef"] : Array.Empty<string>(), Bwrap, unavailableReason: null);

        record.Outcome.ShouldBe(SandboxConfinementOutcome.Confined);
        record.NetworkSevered.ShouldBe(expectedSevered);
        record.EgressSealedToBroker.ShouldBe(expectedSealed, "sealed means severed from everything but the broker, which is what the relay leaves a network-off run");
    }

    [Fact]
    public void An_unconfined_launch_records_that_it_was_unconfined_whatever_its_lease()
    {
        var record = LocalProcessRunner.LaunchConfinement(RelayedSpec(allowNetwork: false), Array.Empty<string>(), bwrap: null, SandboxConfinement.ReasonNotLinux);

        record.Outcome.ShouldBe(SandboxConfinementOutcome.Unconfined);
        record.EgressSealedToBroker.ShouldBeFalse("nothing sealed a run nothing confined");
    }

    private static SandboxSpec RelayedSpec(bool allowNetwork) => new() { Command = "agent", AllowNetwork = allowNetwork, ModelBrokerPort = Port, ModelBrokerSocketPath = Socket };

    /// <summary>The chain this spec launches behind, on a host whose bwrap and prlimit are the given paths (null where absent), under a stand-in cgroup prefix.</summary>
    private static IReadOnlyList<string> Chain(SandboxSpec spec, IReadOnlyList<string> egressPrefix, string? bwrap, string? prlimit = Prlimit) =>
        LocalProcessRunner.ChildCommand(new LocalProcessRunner.CommandIsolationContext(spec, null, null, egressPrefix, ["CGSELF", "x"]), bwrap, prlimit);

    private static int At(IReadOnlyList<string> argv, string token) => argv.ToList().LastIndexOf(token);

    /// <summary>The flags that mount <paramref name="path"/> onto itself, joined; empty when nothing does.</summary>
    private static string BoundAs(IReadOnlyList<string> argv, string path) =>
        string.Join(" ", Enumerable.Range(0, argv.Count - 2).Where(i => argv[i + 1] == path && argv[i + 2] == path).Select(i => argv[i]));
}
