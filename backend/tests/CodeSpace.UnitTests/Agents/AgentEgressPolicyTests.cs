using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the opt-in egress permission (B3.3b): an unset run defaults to <see cref="AgentEgressPolicy.Full"/> (today's
/// behaviour) with no extra hosts, the knob round-trips, and a permissions envelope persisted BEFORE this field existed
/// deserializes to Full — so introducing the knob never silently restricts an existing run's egress.
/// </summary>
[Trait("Category", "Unit")]
public class AgentEgressPolicyTests
{
    [Fact]
    public void Default_permissions_are_full_egress_with_no_extra_hosts()
    {
        var p = new AgentPermissions();

        p.Egress.ShouldBe(AgentEgressPolicy.Full);
        p.EgressAllowHosts.ShouldBeNull();
    }

    [Fact]
    public void Egress_allowlist_with_extra_hosts_round_trips()
    {
        var task = new AgentTask
        {
            Goal = "g",
            Harness = "codex-cli",
            Permissions = new AgentPermissions { Network = AgentNetworkAccess.On, Egress = AgentEgressPolicy.Allowlist, EgressAllowHosts = new[] { "registry.npmjs.org" } },
        };

        var round = JsonSerializer.Deserialize<AgentTask>(JsonSerializer.Serialize(task, AgentJson.Options), AgentJson.Options)!;

        round.Permissions.Egress.ShouldBe(AgentEgressPolicy.Allowlist);
        round.Permissions.EgressAllowHosts.ShouldBe(new[] { "registry.npmjs.org" });
    }

    [Fact]
    public void A_permissions_envelope_without_the_egress_field_deserializes_to_full()
    {
        // An AgentTask persisted before this field existed has no "egress" key — it must default to Full, never to a
        // restricted posture that would sever an in-flight run's network on redeploy.
        var legacy = """{"goal":"g","harness":"codex-cli","permissions":{"network":"On","writeScope":"Workspace"}}""";

        var task = JsonSerializer.Deserialize<AgentTask>(legacy, AgentJson.Options)!;

        task.Permissions.Egress.ShouldBe(AgentEgressPolicy.Full);
        task.Permissions.EgressAllowHosts.ShouldBeNull();
    }

    [Fact]
    public void An_unset_egress_allow_hosts_is_omitted_from_the_persisted_json()
    {
        var task = new AgentTask { Goal = "g", Harness = "codex-cli", Permissions = new AgentPermissions { Network = AgentNetworkAccess.On } };

        JsonSerializer.Serialize(task, AgentJson.Options).ShouldNotContain("egressAllowHosts", Case.Insensitive, "an unset extra-hosts list must add nothing to the envelope");
    }
}
