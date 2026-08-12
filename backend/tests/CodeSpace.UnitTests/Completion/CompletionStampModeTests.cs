using CodeSpace.Core.Services.Completion;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Dtos.Workflows;
using Shouldly;

namespace CodeSpace.UnitTests.Completion;

/// <summary>
/// 🟢 Unit: the P2b cohort stamp — how a NEW run's enforcement mode derives from its definition's own opt-in.
/// Pins: null inherits the platform default (Shadow while the rollout holds), the two vocabulary values map
/// exactly and case-sensitively, and an unreadable value THROWS (a definition whose enforcement vocabulary the
/// policy cannot read never launches — stamping weaker than the author declared is the unacceptable direction).
/// </summary>
[Trait("Category", "Unit")]
public class CompletionStampModeTests
{
    [Fact]
    public void The_platform_default_stays_shadow_while_the_rollout_holds()
    {
        // Flipping this constant is THE platform-wide consumer switch — a deliberate PR, never a side effect.
        CompletionPolicy.CurrentMode.ShouldBe(CompletionEnforcementMode.Shadow);
        CompletionPolicy.StampModeFor(null).ShouldBe(CompletionEnforcementMode.Shadow);
    }

    [Theory]
    [InlineData(WorkflowDefinition.CompletionModeShadow, CompletionEnforcementMode.Shadow)]
    [InlineData(WorkflowDefinition.CompletionModeEnforced, CompletionEnforcementMode.Enforced)]
    public void The_definition_opt_in_maps_exactly(string definitionMode, CompletionEnforcementMode expected)
    {
        CompletionPolicy.StampModeFor(definitionMode).ShouldBe(expected);
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
        Should.Throw<InvalidOperationException>(() => CompletionPolicy.StampModeFor(definitionMode))
            .Message.ShouldContain("refusing to launch");
    }
}
