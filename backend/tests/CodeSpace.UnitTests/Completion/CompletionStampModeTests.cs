using CodeSpace.Core.Services.Completion;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Dtos.Workflows;
using Shouldly;

namespace CodeSpace.UnitTests.Completion;

/// <summary>
/// 🟢 Unit: the P2b cohort stamp + the Q3 admission gate — how a NEW run's enforcement mode derives from its
/// definition's own opt-in AND its operating mode's standing. Pins: null inherits the platform default (Shadow
/// while the rollout holds), 'shadow' maps without consulting the cohort, 'enforced' is a cohort privilege
/// (stamps only for a mode holding Enforceable standing; an unready or unregistered mode refuses to launch,
/// naming what fell short), and an unreadable value THROWS (stamping weaker than the author declared is the
/// unacceptable direction).
/// </summary>
[Trait("Category", "Unit")]
public class CompletionStampModeTests
{
    [Fact]
    public void The_platform_default_stays_shadow_while_the_rollout_holds()
    {
        // Flipping this constant is THE platform-wide consumer switch — a deliberate PR, never a side effect.
        CompletionPolicy.CurrentMode.ShouldBe(CompletionEnforcementMode.Shadow);
        CompletionPolicy.StampModeFor(null, RunModeKeys.Generic, profile: null).ShouldBe(CompletionEnforcementMode.Shadow);
    }

    [Fact]
    public void A_shadow_opt_in_never_consults_the_cohort()
    {
        // Shadow is observation, not privilege — even a mode with no conformance story may opt in.
        CompletionPolicy.StampModeFor(WorkflowDefinition.CompletionModeShadow, RunModeKeys.Generic, profile: null).ShouldBe(CompletionEnforcementMode.Shadow);
    }

    [Fact]
    public void An_enforced_opt_in_stamps_only_for_an_enforceable_mode()
    {
        var registry = new ModeProfileRegistry();

        CompletionPolicy.StampModeFor(WorkflowDefinition.CompletionModeEnforced, RunModeKeys.Supervisor, registry.Resolve(RunModeKeys.Supervisor))
            .ShouldBe(CompletionEnforcementMode.Enforced, "supervisor graduated — the first admitted Enforced cohort");
    }

    [Theory]
    [InlineData(RunModeKeys.SingleAgent, "ProtocolReadiness.Shadow")]              // registered, standing below the bar
    [InlineData(RunModeKeys.PlanMap, "ProtocolReadiness.Open")]
    [InlineData(RunModeKeys.Generic, "no registered conformance profile")]         // no conformance story at all
    public void An_enforced_opt_in_for_an_unready_mode_refuses_to_launch(string mode, string expectedDetail)
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            CompletionPolicy.StampModeFor(WorkflowDefinition.CompletionModeEnforced, mode, new ModeProfileRegistry().Resolve(mode)));

        ex.Message.ShouldContain($"mode '{mode}'");
        ex.Message.ShouldContain(expectedDetail, customMessage: "the refusal must name the standing that fell short — admission is legible, never a mystery throw");
    }

    [Fact]
    public void The_wire_vocabulary_is_pinned()
    {
        // Stored in every opted-in definition's JSON — renaming is a data migration, not a refactor.
        WorkflowDefinition.CompletionModeShadow.ShouldBe("shadow");
        WorkflowDefinition.CompletionModeEnforced.ShouldBe("enforced");
    }

    [Theory]
    [InlineData("Enforced")]   // wrong case is unknown — the vocabulary is exact
    [InlineData("yolo")]
    [InlineData("")]
    public void An_unreadable_opt_in_refuses_to_launch(string definitionMode)
    {
        Should.Throw<InvalidOperationException>(() => CompletionPolicy.StampModeFor(definitionMode, RunModeKeys.Generic, profile: null))
            .Message.ShouldContain("refusing to launch");
    }
}
