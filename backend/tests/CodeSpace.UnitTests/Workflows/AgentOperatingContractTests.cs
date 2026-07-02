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

    [Fact]
    public void Compose_appends_the_contract_after_the_persona_and_bares_it_when_no_persona()
    {
        // B1: the system-prompt text a harness projects natively = persona THEN the always-on contract (contract last so
        // it can't be diluted). A blank/null persona ⇒ the bare contract (byte-identical to a pre-persona run).
        AgentOperatingContract.Compose("You are a reviewer.")
            .ShouldBe("You are a reviewer.\n\n" + AgentOperatingContract.SystemDirective);

        AgentOperatingContract.Compose(null).ShouldBe(AgentOperatingContract.SystemDirective, "no persona → the bare contract");
        AgentOperatingContract.Compose("   ").ShouldBe(AgentOperatingContract.SystemDirective, "a blank persona is the bare contract too");
        AgentOperatingContract.Compose("  You are X.  ").ShouldBe("You are X.\n\n" + AgentOperatingContract.SystemDirective, "the persona is trimmed before composing");
    }
}
