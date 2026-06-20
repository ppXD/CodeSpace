using CodeSpace.Core.Services.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public class AgentOperatingContractTests
{
    [Fact]
    public void System_directive_covers_the_three_unattended_behaviours()
    {
        // The contract's INTENT is pinned (not the exact prose), so a future edit can't silently gut a behaviour: an
        // unattended agent must not wait on stdin (C1 reinforcement), must raise a genuine blocker via the decision tool,
        // and must not end its turn by asking (A2 reinforcement). Dropping one weakens the harness adapter contract.
        var directive = AgentOperatingContract.SystemDirective;

        directive.ShouldNotBeNullOrWhiteSpace();
        directive.ShouldContain("UNATTENDED", Case.Insensitive);
        directive.ShouldContain("stdin", Case.Insensitive);
        directive.ShouldContain("decision tool", Case.Insensitive);
        directive.ShouldContain("question", Case.Insensitive);
    }
}
