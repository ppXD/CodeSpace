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
}
