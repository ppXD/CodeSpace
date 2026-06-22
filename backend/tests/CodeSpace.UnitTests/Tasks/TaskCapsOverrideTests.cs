using CodeSpace.Core.Handlers.CommandHandlers.Tasks;
using CodeSpace.Messages.Tasks;
using Shouldly;

namespace CodeSpace.UnitTests.Tasks;

/// <summary>
/// Pins the operator safety-budget caps DTO (F1): IsEmpty drives the launch service's "leave the router override
/// unset" no-op, and ToRouteCaps projects ONLY the numeric caps onto the router seam — autonomy/approval are left
/// at their defaults so the router's tighten-only merge keeps the preset's values for them (a launch cap can never
/// RAISE autonomy or DROP an approval requirement).
/// </summary>
[Trait("Category", "Unit")]
public class TaskCapsOverrideTests
{
    [Fact]
    public void IsEmpty_is_true_only_when_every_cap_is_unset()
    {
        new TaskCapsOverride().IsEmpty.ShouldBeTrue("all caps null → no override");
        new TaskCapsOverride { MaxCostUsd = 5m }.IsEmpty.ShouldBeFalse();
        new TaskCapsOverride { MaxParallelism = 2 }.IsEmpty.ShouldBeFalse();
        new TaskCapsOverride { MaxRounds = 3 }.IsEmpty.ShouldBeFalse();
        new TaskCapsOverride { MaxTotalSpawns = 4 }.IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public void ToRouteCaps_carries_the_numeric_caps_and_leaves_autonomy_and_approval_at_defaults()
    {
        var caps = new TaskCapsOverride { MaxCostUsd = 12.50m, MaxParallelism = 3, MaxRounds = 7, MaxTotalSpawns = 9 };

        var route = caps.ToRouteCaps();

        route.MaxCostUsd.ShouldBe(12.50m);
        route.MaxParallelism.ShouldBe(3);
        route.MaxRounds.ShouldBe(7);
        route.MaxTotalSpawns.ShouldBe(9);
        route.AutonomyCeiling.ShouldBe("", "a launch cap never sets the autonomy ceiling — the router merge keeps the preset's (tighten-only elsewhere)");
        route.RequiresApproval.ShouldBeFalse("a launch cap never sets approval — the router merge keeps the preset's (OR-only elsewhere)");
    }

    [Fact]
    public void ToRouteCaps_leaves_an_unset_cap_null_so_the_router_keeps_the_preset()
    {
        // Only a cost cap is set; the other three stay null → the router's `@override.X ?? baseCaps.X` keeps the preset.
        var route = new TaskCapsOverride { MaxCostUsd = 2m }.ToRouteCaps();

        route.MaxCostUsd.ShouldBe(2m);
        route.MaxParallelism.ShouldBeNull();
        route.MaxRounds.ShouldBeNull();
        route.MaxTotalSpawns.ShouldBeNull();
    }

    [Theory]
    [InlineData(0)]    // a zero cost cap is meaningless (NormalizeCost downstream would null it → no cap) — reject loud
    [InlineData(-5)]   // a negative cap is a fat-finger — reject loud, never silently degrade to "no cap"
    public void Validate_rejects_a_non_positive_cost_cap(int cost)
    {
        Should.Throw<ArgumentException>(() => new TaskCapsOverride { MaxCostUsd = cost }.Validate());
    }

    [Fact]
    public void Validate_rejects_a_below_one_count_cap()
    {
        Should.Throw<ArgumentException>(() => new TaskCapsOverride { MaxParallelism = 0 }.Validate());
        Should.Throw<ArgumentException>(() => new TaskCapsOverride { MaxTotalSpawns = 0 }.Validate());
        Should.NotThrow(() => new TaskCapsOverride { MaxParallelism = 1, MaxCostUsd = 0.01m }.Validate());
        Should.NotThrow(() => new TaskCapsOverride().Validate());   // all-unset is always valid
    }

    // ─── The handler mapping (LaunchTaskCommand.Caps → TaskLaunchRequest.CapsOverride) — the NEW wiring ───

    [Fact]
    public void BuildCapsOverride_maps_a_set_cap_onto_the_router_seam()
    {
        var caps = LaunchTaskCommandHandler.BuildCapsOverride(new TaskCapsOverride { MaxCostUsd = 4m, MaxParallelism = 3 });

        caps.ShouldNotBeNull();
        caps!.MaxCostUsd.ShouldBe(4m);
        caps.MaxParallelism.ShouldBe(3);
    }

    [Fact]
    public void BuildCapsOverride_collapses_null_or_empty_caps_to_null_the_byte_identical_no_op()
    {
        // The no-op the whole design rests on: no caps ⇒ the router override stays unset ⇒ the preset stands.
        LaunchTaskCommandHandler.BuildCapsOverride(null).ShouldBeNull();
        LaunchTaskCommandHandler.BuildCapsOverride(new TaskCapsOverride()).ShouldBeNull("an all-unset override is treated as no override");
    }

    [Fact]
    public void BuildCapsOverride_fails_loud_on_an_invalid_cap()
    {
        Should.Throw<ArgumentException>(() => LaunchTaskCommandHandler.BuildCapsOverride(new TaskCapsOverride { MaxCostUsd = -1m }));
    }
}
