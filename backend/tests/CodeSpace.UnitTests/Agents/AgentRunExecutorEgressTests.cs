using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the executor's egress wiring (B3.3b): a Full run is byte-identical, an Allowlist run pins its model + git +
/// extra hosts, and — the security-critical case — an Allowlist run whose host set comes out EMPTY is SEVERED, never
/// allowed to fall through to Full egress (which is what an empty allowlist would otherwise derive to).
/// </summary>
[Trait("Category", "Unit")]
public class AgentRunExecutorEgressTests
{
    private static SandboxSpec NetworkedSpec() => new() { Command = "agent", AllowNetwork = true };

    [Fact]
    public void Full_egress_leaves_the_spec_unchanged()
    {
        var spec = NetworkedSpec();
        var perms = new AgentPermissions { Network = AgentNetworkAccess.On, Egress = AgentEgressPolicy.Full };

        var result = AgentRunExecutor.ApplyEgressPolicy(spec, perms, "https://gw.example.com/v1", "Anthropic", null);

        result.ShouldBeSameAs(spec, "Full egress is the default — the spec is returned untouched, byte-identical to today");
        result.EgressAllowlist.ShouldBeNull();
    }

    [Theory]
    [InlineData(false, false, 43121, true, 43121, true)]    // network off + a brokered model → the port and socket the relay carries it through
    [InlineData(false, false, 43121, false, 43121, false)]  // network off, a lease with no socket → the port alone (a confining runner refuses it)
    [InlineData(false, false, null, false, null, false)]    // network off, unbrokered → nothing to reach; severed as always
    [InlineData(true, true, 43121, true, 43121, true)]      // an allowlist → its network is its own, so it is relayed too
    [InlineData(true, false, 43121, true, null, false)]     // network on reaches its broker already, on loopback
    public void Only_a_brokered_run_whose_network_is_its_own_carries_its_broker_channel(bool allowNetwork, bool allowlist, int? brokerPort, bool socket, int? expectedPort, bool expectSocket)
    {
        var spec = new SandboxSpec { Command = "agent", AllowNetwork = allowNetwork, EgressAllowlist = allowlist ? ["gw.example.com"] : null };
        var brokered = brokerPort is null ? null : new BrokeredModelCredential("http://{broker}:43121/r", "token", DateTimeOffset.UtcNow) { RebindPort = brokerPort, SocketPath = socket ? "/spool/k/broker/seg/s" : null };

        var result = AgentRunExecutor.ApplyModelBrokerChannel(spec, brokered);

        result.ModelBrokerPort.ShouldBe(expectedPort);
        result.ModelBrokerSocketPath.ShouldBe(expectSocket ? "/spool/k/broker/seg/s" : null);
        if (expectedPort is null) result.ShouldBeSameAs(spec, "nothing to stamp — the spec is returned untouched");
    }

    [Theory]
    [InlineData(AgentNetworkAccess.Off, AgentEgressPolicy.Full, new string[0])]
    [InlineData(AgentNetworkAccess.On, AgentEgressPolicy.Full, new string[0])]
    [InlineData(AgentNetworkAccess.On, AgentEgressPolicy.Allowlist, new[] { "registry.npmjs.org" })]
    [InlineData(AgentNetworkAccess.On, AgentEgressPolicy.Allowlist, new string[0])]   // an allowlist with nothing derivable: severed
    [InlineData(AgentNetworkAccess.Off, AgentEgressPolicy.Allowlist, new[] { "registry.npmjs.org" })]
    public void The_lease_asks_for_a_socket_exactly_when_the_built_spec_turns_out_to_need_one(AgentNetworkAccess network, AgentEgressPolicy egress, string[] extraHosts)
    {
        // Drift pin: the lease is opened before the spec exists, so it decides from the PERMISSIONS whether the child's
        // network will be its own; the spec's channel is stamped from the SPEC. If the two ever disagree, a child either
        // needs a socket its lease never asked for, or a network-sharing run is handed one it never uses.
        var permissions = new AgentPermissions { Network = network, Egress = egress, EgressAllowHosts = extraHosts };
        var harnessSpec = new SandboxSpec { Command = "agent", AllowNetwork = network == AgentNetworkAccess.On };
        var built = AgentRunExecutor.ApplyEgressPolicy(harnessSpec, permissions, modelBaseUrl: null, modelProvider: null, workspace: null);
        var brokered = new BrokeredModelCredential("http://{broker}:43121/r", "token", DateTimeOffset.UtcNow) { RebindPort = 43121, SocketPath = "/spool/k/broker/seg/s" };

        var stamped = AgentRunExecutor.ApplyModelBrokerChannel(built, brokered).ModelBrokerPort is not null;

        AgentRunExecutor.ChildNetworkIsPrivate(permissions).ShouldBe(stamped, $"permissions {network}/{egress}/[{string.Join(',', extraHosts)}] built to AllowNetwork={built.AllowNetwork}, allowlist=[{string.Join(',', built.EgressAllowlist ?? [])}]");
    }

    [Theory]
    [InlineData(AgentNetworkAccess.Off, AgentEgressPolicy.Full, true)]
    [InlineData(AgentNetworkAccess.On, AgentEgressPolicy.Allowlist, true)]
    [InlineData(AgentNetworkAccess.On, AgentEgressPolicy.Full, false)]   // Trusted: the worker's own network — no socket, nothing changes
    public void A_socket_is_minted_only_on_linux_and_only_for_a_run_whose_network_is_its_own(AgentNetworkAccess network, AgentEgressPolicy egress, bool privateNetwork)
    {
        var runId = Guid.NewGuid();

        var first = AgentRunExecutor.ModelBrokerSocketPathFor(new AgentPermissions { Network = network, Egress = egress }, runId);
        var second = AgentRunExecutor.ModelBrokerSocketPathFor(new AgentPermissions { Network = network, Egress = egress }, runId);

        if (!OperatingSystem.IsLinux() || !privateNetwork)
        {
            first.ShouldBeNull("a host that never confines, or a run that shares the worker's network, mints nothing — its spec, argv and environment stay exactly as they were");
            return;
        }

        first.ShouldNotBeNull().ShouldEndWith("/s");
        Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(first))).ShouldBeOneOf(LocalProcessRunner.ModelBrokerSocketDir, LocalProcessRunner.ModelBrokerShortSocketRoot);
        first.ShouldNotBe(second, "a path serves one lease at a time, so every open mints a fresh, unguessable one");
    }

    [Fact]
    public void Allowlist_pins_the_model_and_extra_hosts()
    {
        var perms = new AgentPermissions { Network = AgentNetworkAccess.On, Egress = AgentEgressPolicy.Allowlist, EgressAllowHosts = new[] { "registry.npmjs.org" } };

        var result = AgentRunExecutor.ApplyEgressPolicy(NetworkedSpec(), perms, "https://gw.example.com/v1", "Anthropic", null);

        result.EgressAllowlist.ShouldBe(new[] { "gw.example.com", "registry.npmjs.org" });
        result.AllowNetwork.ShouldBeTrue("network stays on — the netns filters it, it isn't severed");
    }

    [Fact]
    public void Allowlist_with_no_derivable_host_is_severed_never_full()
    {
        // The security-critical fail-closed case: unknown provider + no base URL + no repo + no extras ⇒ empty host
        // set. SandboxEgressPolicy reads an empty allowlist as "no allowlist → Full", so the wiring MUST sever
        // (AllowNetwork=false) rather than leave a networked run with an empty allowlist that widens to full egress.
        var perms = new AgentPermissions { Network = AgentNetworkAccess.On, Egress = AgentEgressPolicy.Allowlist };

        var result = AgentRunExecutor.ApplyEgressPolicy(NetworkedSpec(), perms, modelBaseUrl: null, modelProvider: "MysteryCo", workspace: null);

        result.AllowNetwork.ShouldBeFalse("an Allowlist run with no derivable host must be SEVERED, never fall through to Full");
        result.EgressAllowlist.ShouldBeNull();

        // Belt-and-suspenders: the resulting policy is None (severed), provably NOT Full.
        SandboxEgressPolicy.Derive(result.AllowNetwork, result.EgressAllowlist, canEnforceAllowlist: true).Mode.ShouldBe(SandboxEgressMode.None);
    }

    [Fact]
    public void The_fail_closed_sever_preserves_other_spec_fields()
    {
        // ApplyEgressPolicy runs AFTER the spec is built with its Mcp wiring + command; the sever path's `with` must
        // carry every untouched field forward (it only flips AllowNetwork + clears the allowlist), or a restricted
        // run with no derivable host would silently lose its tool fabric / command.
        var spec = new SandboxSpec { Command = "agent", Args = new[] { "--x" }, AllowNetwork = true, Mcp = new McpServerWiring { RelativeFileName = ".mcp.json", Content = "{}", SocketPath = "/tmp/s" } };
        var perms = new AgentPermissions { Network = AgentNetworkAccess.On, Egress = AgentEgressPolicy.Allowlist };

        var result = AgentRunExecutor.ApplyEgressPolicy(spec, perms, modelBaseUrl: null, modelProvider: "MysteryCo", workspace: null);

        result.AllowNetwork.ShouldBeFalse();
        result.Command.ShouldBe("agent");
        result.Mcp.ShouldBe(spec.Mcp, "the sever path must preserve the tool-fabric wiring");
    }

    [Fact]
    public void Allowlist_pins_the_git_host_from_the_workspace()
    {
        var workspace = WorkspaceProvisionRequest.FromSingle(new WorkspaceRequest { RepositoryUrl = "https://github.com/owner/repo.git" });
        var perms = new AgentPermissions { Network = AgentNetworkAccess.On, Egress = AgentEgressPolicy.Allowlist };

        var result = AgentRunExecutor.ApplyEgressPolicy(NetworkedSpec(), perms, "https://api.anthropic.com", "Anthropic", workspace);

        result.EgressAllowlist.ShouldBe(new[] { "api.anthropic.com", "github.com" });
    }
}
