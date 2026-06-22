using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
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
    public void Allowlist_pins_the_git_host_from_the_workspace()
    {
        var workspace = WorkspaceProvisionRequest.FromSingle(new WorkspaceRequest { RepositoryUrl = "https://github.com/owner/repo.git" });
        var perms = new AgentPermissions { Network = AgentNetworkAccess.On, Egress = AgentEgressPolicy.Allowlist };

        var result = AgentRunExecutor.ApplyEgressPolicy(NetworkedSpec(), perms, "https://api.anthropic.com", "Anthropic", workspace);

        result.EgressAllowlist.ShouldBe(new[] { "api.anthropic.com", "github.com" });
    }
}
