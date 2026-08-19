using CodeSpace.Core.Services.Completion;
using CodeSpace.Messages.Contracts;
using Shouldly;

namespace CodeSpace.UnitTests.Completion;

/// <summary>
/// 🟢 Unit: the re-derivation of the three STRUCTURAL gates the shadow's recorded would-be terminal decision does
/// not apply, over the REAL registries the terminal authority itself consults. Pins that only a mode holding
/// <see cref="ProtocolReadiness.Enforceable"/> with a registered capability is cohort-eligible, and that a
/// pre-slice row — which records neither — can never read as having cleared a gate.
/// </summary>
[Trait("Category", "Unit")]
public class CompletionCohortEligibilityTests
{
    private static readonly CompletionCohortEligibility Eligibility = new(new CompletionCapabilityRegistry(), new ModeProfileRegistry());

    [Theory]
    [InlineData(RunModeKeys.Supervisor, true)]      // Enforceable — the one graduated cohort
    [InlineData(RunModeKeys.PlanMap, false)]        // Open — the readiness gate parks it Unsupported
    [InlineData(RunModeKeys.SingleAgent, false)]    // Shadow — same
    [InlineData(RunModeKeys.Generic, false)]        // deliberately UNREGISTERED — the mode gate parks it
    [InlineData("no-such-mode", false)]
    public void Only_an_enforceable_mode_is_cohort_eligible(string runMode, bool expected)
    {
        Eligibility.IsCohortEligible(runMode, CapabilityKeys.GitBranch).ShouldBe(expected,
            customMessage: $"'{runMode}' must read cohort-eligible={expected} — the number an Enforced-default decision is argued from counts only runs the authority could actually terminalize");
    }

    [Theory]
    [InlineData(CapabilityKeys.GitBranch, true)]
    [InlineData(CapabilityKeys.GitPatch, true)]
    [InlineData(CapabilityKeys.InlineAnswer, true)]
    [InlineData("spreadsheet", false)]
    public void An_unregistered_capability_is_outside_the_cohort_whatever_the_mode(string capabilityKey, bool expected)
    {
        Eligibility.IsCohortEligible(RunModeKeys.Supervisor, capabilityKey).ShouldBe(expected,
            customMessage: "the authority's FIRST gate is capability registration — a novel ask parks Unsupported even in the graduated lane");
    }

    [Theory]
    [InlineData(null, CapabilityKeys.GitBranch)]
    [InlineData(RunModeKeys.Supervisor, null)]
    [InlineData(null, null)]
    public void A_pre_slice_row_records_no_structural_inputs_and_clears_nothing(string? runMode, string? capabilityKey)
    {
        Eligibility.IsCohortEligible(runMode, capabilityKey).ShouldBeFalse(
            "a row written before the structural columns existed has nothing to re-derive from — reading it as cleared is the overstatement this split exists to remove");
    }
}
