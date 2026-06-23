using CodeSpace.Core.Services.Tasks.Projection.Builders;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions;

/// <summary>
/// Pins <see cref="AgentNodeMapping.ComposeGoal"/> — the shared point that folds a continuing turn's thread-context
/// grounding into the agent / supervisor prompt. The byte-identical guarantee (no grounding ⇒ goal verbatim) is the
/// load-bearing one: a fresh launch must emit the EXACT same agent config it did before the session layer existed.
/// </summary>
[Trait("Category", "Unit")]
public class SessionGroundingComposeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_grounding_returns_the_goal_verbatim(string? grounding) =>
        AgentNodeMapping.ComposeGoal("Fix the retry backoff", grounding)
            .ShouldBe("Fix the retry backoff", "a fresh launch must be byte-identical — the goal is untouched");

    [Fact]
    public void Present_grounding_is_prepended_before_the_goal_with_continue_framing()
    {
        // Grounding first (the agent reads context before the task), the follow-up task last, with framing that
        // tells the agent to build on prior work rather than redo it.
        var result = AgentNodeMapping.ComposeGoal("Add jitter too", grounding: "# Earlier turns\nTurn 1: added backoff");

        result.ShouldStartWith("# Earlier turns");
        result.ShouldEndWith("Add jitter too");
        result.ShouldContain("do not start over");
        result.IndexOf("Turn 1: added backoff", StringComparison.Ordinal)
            .ShouldBeLessThan(result.IndexOf("Add jitter too", StringComparison.Ordinal));
    }
}
