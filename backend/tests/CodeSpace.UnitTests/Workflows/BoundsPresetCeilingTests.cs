using CodeSpace.Core.Services.Tasks.Bounds.Presets.Deep;
using CodeSpace.Core.Services.Tasks.Bounds.Presets.Quick;
using CodeSpace.Core.Services.Tasks.Bounds.Presets.Standard;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the three effort presets' <c>AutonomyCeiling</c> (Rule 8 — now LOAD-BEARING). Before the hard clamp these
/// strings were set-but-never-read; now <c>TaskLaunchService.BuildAgentProfile</c> clamps the operator's requested
/// tier down to them, so a silent change here directly relaxes (or tightens) what a Quick/Standard/Deep task may
/// run. Hard-pin so any change to a ceiling is a deliberate, reviewed security decision — never an invisible edit.
/// The matching parallelism caps are pinned too, since fix #2 makes Standard's MaxParallelism reach the fan-out.
/// </summary>
[Trait("Category", "Unit")]
public class BoundsPresetCeilingTests
{
    [Fact]
    public void Quick_caps_autonomy_at_Standard()
    {
        new QuickBoundsPreset().ToCaps().AutonomyCeiling.ShouldBe("Standard",
            customMessage: "Quick must never run above Standard — this ceiling is now clamped against, not advisory");
    }

    [Fact]
    public void Standard_caps_autonomy_at_Standard_and_parallelism_at_three()
    {
        var caps = new StandardBoundsPreset().ToCaps();

        caps.AutonomyCeiling.ShouldBe("Standard", "the Standard tier is the no-network workspace-write ceiling, now clamped against");
        caps.MaxParallelism.ShouldBe(3, "Standard's parallelism cap is now enforced on the flow.map fan-out (fix #2)");
    }

    [Fact]
    public void Deep_caps_autonomy_at_Standard_and_pins_its_parallelism()
    {
        var caps = new DeepBoundsPreset().ToCaps();

        caps.AutonomyCeiling.ShouldBe("Standard",
            customMessage: "even the generous Deep tier holds autonomy at Standard — raising it is a reviewed decision, not a silent edit");
        caps.MaxParallelism.ShouldBe(5, "Deep's parallelism is load-bearing too — it is frozen into the flow.map config when deep degrades to map-fanout (fix #2)");
    }
}
