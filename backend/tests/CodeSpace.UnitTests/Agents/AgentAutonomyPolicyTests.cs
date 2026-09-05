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
    [InlineData(AgentAutonomyLevel.Trusted, AgentAutonomyLevel.Trusted, Unbounded, "Network: on (Trusted)")]
    [InlineData(AgentAutonomyLevel.Unleashed, AgentAutonomyLevel.Unleashed, Unbounded, "Network: on (Unleashed)")]
    // Policy allowed it; nobody asked. The DEFAULT — an ordinary launch, stated rather than assumed. Every "off"
    // is QUALIFIED: the tier's Network.Off becomes a severed namespace only where bubblewrap actually confines,
    // and Sandbox:RequireConfinement (which would refuse an unconfinable host) defaults to false.
    [InlineData(AgentAutonomyLevel.Standard, AgentAutonomyLevel.Trusted, Unbounded, "Network: off (Standard)" + AgentAutonomyPolicy.ConfinementCaveat)]
    [InlineData(AgentAutonomyLevel.Confined, AgentAutonomyLevel.Trusted, Unbounded, "Network: off (Confined)" + AgentAutonomyPolicy.ConfinementCaveat)]
    // The ceiling cannot reach a network-granting tier: this run could NOT have had network however it was
    // launched. Distinct wording from "off" on purpose — declined and denied are different facts.
    [InlineData(AgentAutonomyLevel.Standard, AgentAutonomyLevel.Standard, Unbounded, "Network: clamped off by policy (ceiling Standard)" + AgentAutonomyPolicy.ConfinementCaveat)]
    [InlineData(AgentAutonomyLevel.Confined, AgentAutonomyLevel.Confined, Unbounded, "Network: clamped off by policy (ceiling Confined)" + AgentAutonomyPolicy.ConfinementCaveat)]
    // B6: the DEPLOYMENT's own ceiling denied it. Named ahead of the route's, because it is the one bound the
    // operator cannot lift by relaunching at a different effort tier — the route ceiling below is Trusted, which
    // would have granted network, and the run still had none.
    [InlineData(AgentAutonomyLevel.Standard, AgentAutonomyLevel.Trusted, AgentAutonomyLevel.Standard, "Network: clamped off by deployment ceiling (Standard)" + AgentAutonomyPolicy.ConfinementCaveat)]
    [InlineData(AgentAutonomyLevel.Confined, AgentAutonomyLevel.Trusted, AgentAutonomyLevel.Confined, "Network: clamped off by deployment ceiling (Confined)" + AgentAutonomyPolicy.ConfinementCaveat)]
    // Both bind: the deployment ceiling wins the sentence, since switching effort tier would not have helped.
    [InlineData(AgentAutonomyLevel.Standard, AgentAutonomyLevel.Standard, AgentAutonomyLevel.Standard, "Network: clamped off by deployment ceiling (Standard)" + AgentAutonomyPolicy.ConfinementCaveat)]
    // A deployment ceiling that DOES grant network changes nothing — it is not the binding bound.
    [InlineData(AgentAutonomyLevel.Standard, AgentAutonomyLevel.Trusted, AgentAutonomyLevel.Trusted, "Network: off (Standard)" + AgentAutonomyPolicy.ConfinementCaveat)]
    // A run that predates a lowered ceiling and really DID have network still reads "on" — the effective tier is
    // the run's own record, never re-derived from today's setting.
    [InlineData(AgentAutonomyLevel.Trusted, AgentAutonomyLevel.Trusted, AgentAutonomyLevel.Standard, "Network: on (Trusted)")]
    public void DescribeNetwork_states_the_effective_posture_and_who_decided(AgentAutonomyLevel effective, AgentAutonomyLevel ceiling, AgentAutonomyLevel deploymentCeiling, string expected)
    {
        AgentAutonomyPolicy.DescribeNetwork(effective, ceiling, deploymentCeiling).ShouldBe(expected,
            customMessage: "the journal sentence is derived from the SAME Derive table the sandbox enforces — it must never claim a posture the runner does not have");
    }

    /// <summary>The committed deployment ceiling, spelled out at every call site that is NOT exercising the deployment bound — an attribute argument cannot call <c>AgentAutonomyPolicy.DefaultDeploymentCeiling</c>'s name through another const without this alias.</summary>
    private const AgentAutonomyLevel Unbounded = AgentAutonomyPolicy.DefaultDeploymentCeiling;

    [Fact]
    public void DescribeNetwork_never_says_on_for_a_tier_Derive_leaves_severed()
    {
        // The load-bearing invariant: the sentence is a projection of Derive, not a parallel table. Whichever tiers
        // Derive grants network to are exactly the tiers that may read "on" — so adding a tier cannot desync them.
        foreach (var tier in Enum.GetValues<AgentAutonomyLevel>())
        {
            var saysOn = AgentAutonomyPolicy.DescribeNetwork(tier, AgentAutonomyLevel.Unleashed, AgentAutonomyLevel.Unleashed).Contains("on (");

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

                foreach (var deployment in Enum.GetValues<AgentAutonomyLevel>())
                    AgentAutonomyPolicy.DescribeNetwork(effective, ceiling, deployment).ShouldEndWith(AgentAutonomyPolicy.ConfinementCaveat,
                        customMessage: $"'{effective}' under ceiling '{ceiling}' / deployment ceiling '{deployment}' claims a severed network the sandbox is not guaranteed to have applied");
            }
        }
    }

    [Theory]
    // Confinement APPLIED and the netns severed: the hedge is retired — this run provably had no egress.
    [InlineData(SandboxConfinementOutcome.Confined, null, true, "Network: off (Standard) — confined: egress severed")]
    // Confined but sharing a network (an inherited filtered netns): confined is true, severed is not. Say both.
    [InlineData(SandboxConfinementOutcome.Confined, null, false, "Network: off (Standard) — confined, but egress was NOT severed")]
    // The whole point: the host could NOT confine, so the tier's "off" was never enforced. Stated LOUDLY and with
    // the reason — a reader must not be able to skim this as a milder flavour of "severed".
    [InlineData(SandboxConfinementOutcome.Unconfined, SandboxConfinement.ReasonNotLinux, false, "Network: off (Standard) — OFF REQUESTED BUT UNCONFINED: this host cannot sever egress (not-linux)")]
    [InlineData(SandboxConfinementOutcome.Unconfined, SandboxConfinement.ReasonNoBubblewrap, false, "Network: off (Standard) — OFF REQUESTED BUT UNCONFINED: this host cannot sever egress (no-bwrap)")]
    [InlineData(SandboxConfinementOutcome.Unconfined, SandboxConfinement.ReasonNoUserNamespaces, false, "Network: off (Standard) — OFF REQUESTED BUT UNCONFINED: this host cannot sever egress (no-userns)")]
    // A runner that attempts no confinement at all is in the same honest bucket as one that could not.
    [InlineData(SandboxConfinementOutcome.NotApplicable, null, false, "Network: off (Standard) — OFF REQUESTED BUT UNCONFINED: this runner applies no confinement")]
    public void DescribeNetwork_resolves_the_hedge_from_the_runs_own_confinement_record(SandboxConfinementOutcome outcome, string? reason, bool severed, string expected)
    {
        var confinement = new SandboxConfinement { Outcome = outcome, Reason = reason, NetworkSevered = severed };

        AgentAutonomyPolicy.DescribeNetwork(AgentAutonomyLevel.Standard, AgentAutonomyLevel.Trusted, AgentAutonomyPolicy.DefaultDeploymentCeiling, confinement).ShouldBe(expected,
            customMessage: "a run that RECORDED its posture must be described by that record, not by the hedge the record exists to retire");
    }

    [Fact]
    public void DescribeNetwork_falls_back_to_the_caveat_when_no_posture_was_recorded()
    {
        // The mutation guard: drop the record write (or read a pre-0194 run) and every "off" sentence must return to
        // the hedge — never to an unqualified, un-evidenced "off". Explicit null and the defaulted overload alike.
        AgentAutonomyPolicy.DescribeNetwork(AgentAutonomyLevel.Standard, AgentAutonomyLevel.Trusted, AgentAutonomyPolicy.DefaultDeploymentCeiling, null)
            .ShouldBe("Network: off (Standard)" + AgentAutonomyPolicy.ConfinementCaveat);

        AgentAutonomyPolicy.DescribeNetwork(AgentAutonomyLevel.Standard, AgentAutonomyLevel.Trusted, AgentAutonomyPolicy.DefaultDeploymentCeiling)
            .ShouldBe("Network: off (Standard)" + AgentAutonomyPolicy.ConfinementCaveat);
    }

    [Fact]
    public void DescribeNetwork_states_an_unconfined_posture_loudly_enough_to_be_unmissable()
    {
        // A resolved sentence must never merely APPEND to the hedge: the two say different things, and a reader who
        // sees both learns nothing. Assert the replacement, on every off-tier pair and every unconfined shape.
        foreach (var confinement in new[]
                 {
                     new SandboxConfinement { Outcome = SandboxConfinementOutcome.Unconfined, Reason = SandboxConfinement.ReasonNoUserNamespaces },
                     new SandboxConfinement { Outcome = SandboxConfinementOutcome.NotApplicable },
                 })
        {
            foreach (var effective in Enum.GetValues<AgentAutonomyLevel>())
            {
                foreach (var ceiling in Enum.GetValues<AgentAutonomyLevel>())
                {
                    if (AgentAutonomyPolicy.Derive(effective).Network == AgentNetworkAccess.On) continue;

                    var line = AgentAutonomyPolicy.DescribeNetwork(effective, ceiling, AgentAutonomyPolicy.DefaultDeploymentCeiling, confinement);

                    line.ShouldContain("UNCONFINED", customMessage: $"'{effective}'/'{ceiling}' hides an unenforced 'off' behind quiet wording");
                    line.ShouldNotContain(AgentAutonomyPolicy.ConfinementCaveat, customMessage: "the resolved sentence must REPLACE the hedge, not stack on it");
                }
            }
        }
    }
}
