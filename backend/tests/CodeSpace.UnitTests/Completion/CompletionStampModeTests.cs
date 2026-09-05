using CodeSpace.Core.Services.Completion;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Dtos.Workflows;
using Shouldly;

namespace CodeSpace.UnitTests.Completion;

/// <summary>
/// 🟢 Unit: the P2b cohort stamp + the Q3 admission gate — how a NEW run's enforcement mode derives from its
/// definition's own opt-in AND its operating mode's standing. Pins: null takes the C5 DEFAULT (the mode's own
/// standing — Enforced for an Enforceable mode, the Shadow fallback for everything below the bar, and the same
/// predicate the terminal authority arbitrates by), 'shadow' maps without consulting the cohort — an explicit
/// request wins in both directions — 'enforced' is a cohort privilege
/// (stamps only for a mode holding Enforceable standing; an unready or unregistered mode refuses to launch,
/// naming what fell short), and an unreadable value THROWS (stamping weaker than the author declared is the
/// unacceptable direction).
/// </summary>
[Trait("Category", "Unit")]
public class CompletionStampModeTests
{
    [Fact]
    public void The_fallback_default_stays_shadow_for_a_mode_below_the_bar()
    {
        // C5: the constant is no longer the whole default — it is the FALLBACK a mode without Enforceable
        // standing lands on. Flipping it is still a deliberate PR, never a side effect.
        CompletionPolicy.CurrentMode.ShouldBe(CompletionEnforcementMode.Shadow);
        CompletionPolicy.StampModeFor(null, RunModeKeys.Generic, profile: null).ShouldBe(CompletionEnforcementMode.Shadow);
    }

    [Theory]
    // C5: a run carrying NO opt-in inherits its own operating mode's standing — Enforceable means the completion
    // authority is the default terminal owner, everything below the bar keeps the Shadow fallback.
    [InlineData(RunModeKeys.Supervisor, null, CompletionEnforcementMode.Enforced)]
    [InlineData(RunModeKeys.PlanMap, null, CompletionEnforcementMode.Shadow)]           // ProtocolReadiness.Open
    [InlineData(RunModeKeys.SingleAgent, null, CompletionEnforcementMode.Shadow)]       // ProtocolReadiness.Shadow
    [InlineData(RunModeKeys.Generic, null, CompletionEnforcementMode.Shadow)]           // unregistered — no conformance story
    // …and an EXPLICIT request still wins in both directions, on the very mode the default would have enforced.
    [InlineData(RunModeKeys.Supervisor, WorkflowDefinition.CompletionModeShadow, CompletionEnforcementMode.Shadow)]
    [InlineData(RunModeKeys.Supervisor, WorkflowDefinition.CompletionModeEnforced, CompletionEnforcementMode.Enforced)]
    [InlineData(RunModeKeys.PlanMap, WorkflowDefinition.CompletionModeShadow, CompletionEnforcementMode.Shadow)]
    public void A_run_without_an_opt_in_inherits_its_modes_standing(string mode, string? definitionCompletionMode, CompletionEnforcementMode expected)
    {
        CompletionPolicy.StampModeFor(definitionCompletionMode, mode, new ModeProfileRegistry().Resolve(mode)).ShouldBe(expected);
    }

    [Fact]
    public void The_default_rides_the_terminal_authoritys_own_readiness_predicate()
    {
        // The stamp and the arbitration must never disagree: a run stamped Enforced by default whose mode the
        // authority then reads as below the bar would park Unsupported forever — a cohort of one, unarbitrable.
        // This pins the stamp against the authority's LITERAL gate (CompletionTerminalAuthority: readiness must
        // be Enforceable), over the whole registered vocabulary, so adding a mode cannot skip the question.
        var registry = new ModeProfileRegistry();

        foreach (var mode in registry.RegisteredModes)
        {
            var profile = registry.Resolve(mode).ShouldNotBeNull();
            var stamped = CompletionPolicy.StampModeFor(null, mode, profile);

            (stamped == CompletionEnforcementMode.Enforced).ShouldBe(profile.Readiness == ProtocolReadiness.Enforceable,
                customMessage: $"mode '{mode}' holds ProtocolReadiness.{profile.Readiness} but defaults to {stamped} — the default stamp and the authority's readiness gate have diverged");
        }
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
