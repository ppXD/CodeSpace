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
    public void Standard_caps_autonomy_at_Trusted_and_parallelism_at_three()
    {
        var caps = new StandardBoundsPreset().ToCaps();

        caps.AutonomyCeiling.ShouldBe("Trusted",
            customMessage: "Standard's ceiling admits the operator's explicit network choice (Trusted is the first tier Derive gives Network.On) — it does NOT change the posture an unasked launch gets, which stays the recipes' recommended Standard");
        caps.MaxParallelism.ShouldBe(3, "Standard's parallelism cap is now enforced on the flow.map fan-out (fix #2)");
    }

    [Fact]
    public void Deep_caps_autonomy_at_Trusted_and_pins_its_parallelism()
    {
        var caps = new DeepBoundsPreset().ToCaps();

        caps.AutonomyCeiling.ShouldBe("Trusted",
            customMessage: "Deep admits the same explicit network choice as Standard — and no preset reaches Unleashed; raising a ceiling is a reviewed decision, not a silent edit");
        caps.MaxParallelism.ShouldBe(5, "Deep's parallelism is load-bearing too — it is frozen into the flow.map config when deep degrades to map-fanout (fix #2)");
        caps.MaxTotalSpawns.ShouldBeNull("the tier no longer caps total spawns — concurrency (MaxParallelism) is the only agent knob");
    }

    [Fact]
    public void No_preset_ever_reaches_Unleashed()
    {
        // The one bound that survives the Trusted raise: Unleashed (AgentToolGate's "Allow without approval" tier)
        // is unreachable from ANY launch, on every preset. A new preset that names it must fail here first.
        string[] ceilings = [new QuickBoundsPreset().ToCaps().AutonomyCeiling, new StandardBoundsPreset().ToCaps().AutonomyCeiling, new DeepBoundsPreset().ToCaps().AutonomyCeiling];

        ceilings.ShouldNotContain("Unleashed", "no effort tier may hand a launch the unapproved-side-effect tier");
    }
}
