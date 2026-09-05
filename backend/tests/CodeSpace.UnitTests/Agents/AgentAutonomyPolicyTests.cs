using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the autonomy-tier → sandbox-knobs table. This mapping is operator-facing security policy: a silent
/// change to what "Trusted" grants is a reviewed decision, so the table is hard-pinned (Rule 8 spirit) and the
/// enum-count guard forces a new tier to ship with its own pinned row rather than fall through the safe default.
/// </summary>
[Trait("Category", "Unit")]
public class AgentAutonomyPolicyTests
{
    [Theory]
    [InlineData(AgentAutonomyLevel.Confined, AgentNetworkAccess.Off, AgentWriteScope.ReadOnly)]
    [InlineData(AgentAutonomyLevel.Standard, AgentNetworkAccess.Off, AgentWriteScope.Workspace)]
    [InlineData(AgentAutonomyLevel.Trusted, AgentNetworkAccess.On, AgentWriteScope.Workspace)]
    [InlineData(AgentAutonomyLevel.Unleashed, AgentNetworkAccess.On, AgentWriteScope.Workspace)]
    public void Derive_pins_each_tier_to_its_knobs(AgentAutonomyLevel level, AgentNetworkAccess network, AgentWriteScope writeScope)
    {
        var permissions = AgentAutonomyPolicy.Derive(level);

        permissions.Network.ShouldBe(network);
        permissions.WriteScope.ShouldBe(writeScope);

        // The egress governance knob defaults Full at EVERY tier — opting a run into deny-by-default egress is a
        // per-field override, never a tier default. Pinning it here keeps that a reviewed, non-breaking decision: no
        // tier silently restricts egress (which would break dependency-fetching runs).
        permissions.Egress.ShouldBe(AgentEgressPolicy.Full);
        permissions.EgressAllowHosts.ShouldBeNull();
    }

    [Fact]
    public void Standard_equals_the_historical_permission_default_so_existing_runs_are_unchanged()
    {
        // The pre-dial default was Network=Off + WriteScope=Workspace (new AgentPermissions()). Standard MUST equal
        // it, or introducing the autonomy dial silently changes every run that set neither network nor readOnly.
        AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Standard).ShouldBe(new AgentPermissions());
    }

    [Fact]
    public void Every_tier_is_pinned_so_a_new_one_cannot_ship_unmapped()
    {
        // Adding a tier means: add its InlineData row above AND bump this count — a deliberate, reviewed step,
        // never a silent fall-through to Derive's safe-default arm.
        Enum.GetValues<AgentAutonomyLevel>().Length.ShouldBe(4);
    }

    [Theory]
    // Requested ABOVE the ceiling → clamped DOWN to the ceiling (the privilege-escalation hole this closes).
    [InlineData(AgentAutonomyLevel.Unleashed, AgentAutonomyLevel.Standard, AgentAutonomyLevel.Standard)]
    [InlineData(AgentAutonomyLevel.Trusted, AgentAutonomyLevel.Standard, AgentAutonomyLevel.Standard)]
    [InlineData(AgentAutonomyLevel.Unleashed, AgentAutonomyLevel.Confined, AgentAutonomyLevel.Confined)]
    // Requested AT or BELOW the ceiling → kept verbatim (the clamp never escalates, never tightens what's already safe).
    [InlineData(AgentAutonomyLevel.Confined, AgentAutonomyLevel.Trusted, AgentAutonomyLevel.Confined)]
    [InlineData(AgentAutonomyLevel.Standard, AgentAutonomyLevel.Standard, AgentAutonomyLevel.Standard)]
    [InlineData(AgentAutonomyLevel.Standard, AgentAutonomyLevel.Unleashed, AgentAutonomyLevel.Standard)]
    // No ceiling (the top tier) → a no-op, the request passes through unchanged.
    [InlineData(AgentAutonomyLevel.Trusted, AgentAutonomyLevel.Unleashed, AgentAutonomyLevel.Trusted)]
    public void Clamp_takes_the_lower_of_requested_and_ceiling(AgentAutonomyLevel requested, AgentAutonomyLevel ceiling, AgentAutonomyLevel expected)
    {
        AgentAutonomyPolicy.Clamp(requested, ceiling).ShouldBe(expected);
    }

    [Fact]
    public void Clamp_is_symmetric_in_its_arguments_since_it_is_the_min()
    {
        // Defensive: the clamp is order-independent (it's Math.Min over the ints), so swapping requested/ceiling
        // yields the same tier. Pins that the enum stays ASCENDING by privilege — if a future reorder broke that,
        // Min would silently pick the wrong tier and this guards against it together with the Theory above.
        AgentAutonomyPolicy.Clamp(AgentAutonomyLevel.Unleashed, AgentAutonomyLevel.Standard)
            .ShouldBe(AgentAutonomyPolicy.Clamp(AgentAutonomyLevel.Standard, AgentAutonomyLevel.Unleashed));
    }

    // ── B5: the journal's network-posture sentence (the run's honest answer to "did these agents have the internet?") ──

    [Theory]
    // Policy ALLOWED network and the launcher asked for it.
    [InlineData(AgentAutonomyLevel.Trusted, AgentAutonomyLevel.Trusted, "Network: on (Trusted)")]
    [InlineData(AgentAutonomyLevel.Unleashed, AgentAutonomyLevel.Unleashed, "Network: on (Unleashed)")]
    // Policy allowed it; nobody asked. The DEFAULT — an ordinary launch, stated rather than assumed. Every "off"
    // is QUALIFIED: the tier's Network.Off becomes a severed namespace only where bubblewrap actually confines,
    // and Sandbox:RequireConfinement (which would refuse an unconfinable host) defaults to false.
    [InlineData(AgentAutonomyLevel.Standard, AgentAutonomyLevel.Trusted, "Network: off (Standard)" + AgentAutonomyPolicy.ConfinementCaveat)]
    [InlineData(AgentAutonomyLevel.Confined, AgentAutonomyLevel.Trusted, "Network: off (Confined)" + AgentAutonomyPolicy.ConfinementCaveat)]
    // The ceiling cannot reach a network-granting tier: this run could NOT have had network however it was
    // launched. Distinct wording from "off" on purpose — declined and denied are different facts.
    [InlineData(AgentAutonomyLevel.Standard, AgentAutonomyLevel.Standard, "Network: clamped off by policy (ceiling Standard)" + AgentAutonomyPolicy.ConfinementCaveat)]
    [InlineData(AgentAutonomyLevel.Confined, AgentAutonomyLevel.Confined, "Network: clamped off by policy (ceiling Confined)" + AgentAutonomyPolicy.ConfinementCaveat)]
    public void DescribeNetwork_states_the_effective_posture_and_who_decided(AgentAutonomyLevel effective, AgentAutonomyLevel ceiling, string expected)
    {
        AgentAutonomyPolicy.DescribeNetwork(effective, ceiling).ShouldBe(expected,
            customMessage: "the journal sentence is derived from the SAME Derive table the sandbox enforces — it must never claim a posture the runner does not have");
    }

    [Fact]
    public void DescribeNetwork_never_says_on_for_a_tier_Derive_leaves_severed()
    {
        // The load-bearing invariant: the sentence is a projection of Derive, not a parallel table. Whichever tiers
        // Derive grants network to are exactly the tiers that may read "on" — so adding a tier cannot desync them.
        foreach (var tier in Enum.GetValues<AgentAutonomyLevel>())
        {
            var saysOn = AgentAutonomyPolicy.DescribeNetwork(tier, AgentAutonomyLevel.Unleashed).Contains("on (");

            saysOn.ShouldBe(AgentAutonomyPolicy.Derive(tier).Network == AgentNetworkAccess.On, $"the '{tier}' sentence must agree with Derive('{tier}')");
        }
    }

    [Fact]
    public void DescribeNetwork_never_claims_an_unqualified_off()
    {
        // The claim this qualifier ends: "off" is the TIER'S PERMISSION, and it becomes a severed network namespace
        // only where LocalProcessRunner rewrites the command through bubblewrap (BubblewrapSandbox.Available — absent
        // on macOS development, on a host without bwrap, on one denying unprivileged user namespaces), and
        // Sandbox:RequireConfinement — the setting that would refuse an unconfinable host — defaults to false. So
        // EVERY sentence saying a run had no network carries the caveat, on every tier pair, not just the sampled ones.
        foreach (var effective in Enum.GetValues<AgentAutonomyLevel>())
        {
            foreach (var ceiling in Enum.GetValues<AgentAutonomyLevel>())
            {
                if (AgentAutonomyPolicy.Derive(effective).Network == AgentNetworkAccess.On) continue;

                AgentAutonomyPolicy.DescribeNetwork(effective, ceiling).ShouldEndWith(AgentAutonomyPolicy.ConfinementCaveat,
                    customMessage: $"'{effective}' under ceiling '{ceiling}' claims a severed network the sandbox is not guaranteed to have applied");
            }
        }
    }
}
