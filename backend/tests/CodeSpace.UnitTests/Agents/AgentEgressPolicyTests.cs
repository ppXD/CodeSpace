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
    public void A_full_egress_run_serializes_byte_identically_to_a_pre_field_envelope()
    {
        // The default (Full) egress + unset extra-hosts must add NOTHING to the persisted JSON, so an existing run's
        // task_json is unchanged by introducing the knob — no "egress" key, no "egressAllowHosts" key.
        var task = new AgentTask { Goal = "g", Harness = "codex-cli", Permissions = new AgentPermissions { Network = AgentNetworkAccess.On } };

        var json = JsonSerializer.Serialize(task, AgentJson.Options);

        json.ShouldNotContain("egress", Case.Insensitive, "a Full (default) run must add neither the egress posture nor the extra-hosts list to the envelope");
    }

    [Fact]
    public void An_allowlist_run_does_serialize_the_egress_posture()
    {
        var task = new AgentTask { Goal = "g", Harness = "codex-cli", Permissions = new AgentPermissions { Network = AgentNetworkAccess.On, Egress = AgentEgressPolicy.Allowlist } };

        JsonSerializer.Serialize(task, AgentJson.Options).ShouldContain("Allowlist", Case.Insensitive, "a non-default posture IS persisted so the run's intent survives a re-attach");
    }
}
