using CodeSpace.Core.Services.Decisions;
using Shouldly;

namespace CodeSpace.UnitTests.Decisions;

/// <summary>
/// 🟢 Unit: the per-run pending-decision guardrail (<see cref="DecisionBounds"/>, D5c) — the pure admit/refuse boundary
/// + the env-overridable cap (Rule 8). Pins: under the cap admits, at the cap the next raise is refused with a message
/// naming the env var; the cap parses + clamps; and the env-var constant name is hard-pinned (a rename breaks any
/// operator who tuned it).
/// </summary>
[Trait("Category", "Unit")]
public class DecisionBoundsTests
{
    [Fact]
    public void Under_the_cap_admits_and_at_the_cap_the_next_raise_is_refused()
    {
        DecisionBounds.PendingCapBreach(0).ShouldBeNull("no other pending → admit");
        DecisionBounds.PendingCapBreach(DecisionBounds.DefaultMaxPendingPerRun - 1).ShouldBeNull("one under the default cap → the next is admitted");

        var breach = DecisionBounds.PendingCapBreach(DecisionBounds.DefaultMaxPendingPerRun);
        breach.ShouldNotBeNull("at the cap the next raise is refused");
        breach!.ShouldContain(DecisionBounds.MaxPendingPerRunEnvVar, customMessage: "the breach names the env var to raise");
    }

    [Theory]
    [InlineData(null, 3)]            // unset → default
    [InlineData("not-a-number", 3)] // unparseable → default
    [InlineData("5", 5)]
    [InlineData("0", 1)]            // clamp to the floor
    [InlineData("999999", 1000)]   // clamp to the ceiling
    public void ParseCap_uses_the_default_or_clamps_to_range(string? raw, int expected) =>
        DecisionBounds.ParseCap(raw, DecisionBounds.DefaultMaxPendingPerRun).ShouldBe(expected);

    [Fact]
    public void MaxPendingPerRunEnvVar_constant_name_is_pinned() =>
        // A rename breaks every operator who pinned the per-run cap via env. Hard-pin (Rule 8).
        DecisionBounds.MaxPendingPerRunEnvVar.ShouldBe("CODESPACE_DECISION_MAX_PENDING_PER_RUN");

    [Fact]
    public void The_env_override_raises_the_cap()
    {
        var previous = Environment.GetEnvironmentVariable(DecisionBounds.MaxPendingPerRunEnvVar);

        try
        {
            Environment.SetEnvironmentVariable(DecisionBounds.MaxPendingPerRunEnvVar, "10");

            DecisionBounds.PendingCapBreach(DecisionBounds.DefaultMaxPendingPerRun).ShouldBeNull("with the cap raised to 10, 3 other pending is under it");
            DecisionBounds.PendingCapBreach(10).ShouldNotBeNull("at the raised cap of 10 the next is refused");
        }
        finally
        {
            Environment.SetEnvironmentVariable(DecisionBounds.MaxPendingPerRunEnvVar, previous);
        }
    }
}
