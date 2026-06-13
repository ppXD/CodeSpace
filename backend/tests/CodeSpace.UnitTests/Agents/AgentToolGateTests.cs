using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the autonomy×risk → governance verdict table: a non-approval (read-only) tool always runs; a gated
/// (destructive) tool is denied at Confined, needs approval at Standard/Trusted, runs only at Unleashed; and an
/// unknown tier fails closed (denies a gated tool). Any change to this table is a deliberate, reviewed decision.
/// </summary>
[Trait("Category", "Unit")]
public class AgentToolGateTests
{
    [Theory]
    [InlineData(AgentAutonomyLevel.Confined)]
    [InlineData(AgentAutonomyLevel.Standard)]
    [InlineData(AgentAutonomyLevel.Trusted)]
    [InlineData(AgentAutonomyLevel.Unleashed)]
    public void A_tool_that_needs_no_approval_always_runs(AgentAutonomyLevel level)
    {
        AgentToolGate.Decide(level, requiresApproval: false).ShouldBe(AgentToolGateDecision.Allow);
    }

    [Theory]
    [InlineData(AgentAutonomyLevel.Confined, AgentToolGateDecision.Deny)]
    [InlineData(AgentAutonomyLevel.Standard, AgentToolGateDecision.RequireApproval)]
    [InlineData(AgentAutonomyLevel.Trusted, AgentToolGateDecision.RequireApproval)]
    [InlineData(AgentAutonomyLevel.Unleashed, AgentToolGateDecision.Allow)]
    public void A_gated_tool_follows_the_tier_ladder(AgentAutonomyLevel level, AgentToolGateDecision expected)
    {
        AgentToolGate.Decide(level, requiresApproval: true).ShouldBe(expected);
    }

    [Fact]
    public void An_unknown_tier_denies_a_gated_tool_fail_closed()
    {
        AgentToolGate.Decide((AgentAutonomyLevel)999, requiresApproval: true).ShouldBe(AgentToolGateDecision.Deny);
    }

    [Fact]
    public void An_unknown_tier_still_allows_a_safe_tool()
    {
        // A read-only / un-gated tool is harmless regardless of tier — only gated tools fail closed.
        AgentToolGate.Decide((AgentAutonomyLevel)999, requiresApproval: false).ShouldBe(AgentToolGateDecision.Allow);
    }

    [Fact]
    public void The_csharp_default_tier_is_Confined_so_a_defaulted_session_fails_closed()
    {
        default(AgentAutonomyLevel).ShouldBe(AgentAutonomyLevel.Confined);
        AgentToolGate.Decide(default, requiresApproval: true).ShouldBe(AgentToolGateDecision.Deny);
    }
}
