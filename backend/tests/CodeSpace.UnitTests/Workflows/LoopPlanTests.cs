using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Messages.Dtos.Workflows;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins <see cref="LoopPlan.From"/> — the single place a flow.loop's raw config is clamped into a
/// safe runaway plan (iteration cap + hard ceilings) and where the lenient error-handling parse
/// lives. The engine trusts ONLY this struct, so the clamp and the "unknown ⇒ Terminate" safe
/// default (which must never throw on a typo) are pinned here.
/// </summary>
[Trait("Category", "Unit")]
public class LoopPlanTests
{
    [Theory]
    [InlineData(null, LoopErrorHandling.Terminate)]          // unset ⇒ safe default
    [InlineData("", LoopErrorHandling.Terminate)]            // empty ⇒ safe default
    [InlineData("terminate", LoopErrorHandling.Terminate)]   // explicit terminate
    [InlineData("continue", LoopErrorHandling.Continue)]     // explicit continue
    [InlineData("Continue", LoopErrorHandling.Continue)]     // case-insensitive
    [InlineData("CONTINUE", LoopErrorHandling.Continue)]
    [InlineData("skip-please", LoopErrorHandling.Terminate)] // typo ⇒ safe default, never throws
    public void Parses_error_handling_with_a_safe_default(string? raw, LoopErrorHandling expected) =>
        LoopPlan.From(new LoopConfig { ErrorHandling = raw }).ErrorHandling.ShouldBe(expected);

    [Theory]
    [InlineData(0, 1)]                                  // zero ⇒ at least one pass
    [InlineData(-5, 1)]                                 // negative ⇒ clamped up to 1
    [InlineData(10, 10)]                                // in range ⇒ unchanged
    [InlineData(5000, LoopPlan.MaxIterationsCeiling)]   // over ceiling ⇒ capped
    public void Clamps_max_iterations_into_the_safe_range(int configured, int expected) =>
        LoopPlan.From(new LoopConfig { MaxIterations = configured }).MaxIterations.ShouldBe(expected);

    [Fact]
    public void Carries_the_hard_ceilings_every_loop_is_bounded_by()
    {
        var plan = LoopPlan.From(new LoopConfig());

        plan.WallClock.ShouldBe(LoopPlan.WallClockBudget);
        plan.NodeBudget.ShouldBe(LoopPlan.NodeExecutionBudget);
    }
}
