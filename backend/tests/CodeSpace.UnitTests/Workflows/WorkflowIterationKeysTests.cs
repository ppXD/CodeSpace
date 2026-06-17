using CodeSpace.Messages.Constants;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Rule 8 — pin the top-level iteration-key sentinel by value. It is the discriminator the engine writes
/// into <c>workflow_run_node.iteration_key</c> for every non-iteration node, and the EXACT value the
/// from-node rerun seeder + reusability resolver query against to find kept top-level cells. If it ever
/// drifted from the empty string, those rerun queries would silently match zero rows (seeding nothing,
/// degenerating a rerun into a from-root re-run) with no compile error. Hard-pin the literal here so a
/// change is a compile-error-visible decision, not an invisible regression.
/// </summary>
[Trait("Category", "Unit")]
public class WorkflowIterationKeysTests
{
    [Fact]
    public void TopLevel_is_the_empty_string()
    {
        WorkflowIterationKeys.TopLevel.ShouldBe("",
            "the top-level sentinel must stay the empty string — the engine, the rerun seeder, and the rerun " +
            "reusability resolver all match on it; any drift silently breaks from-node rerun's cell pre-seed.");
    }
}
