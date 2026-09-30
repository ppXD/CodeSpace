using System.Text;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// What <c>git.integrate_run</c> says about its own outcome: the text the plan-map synth is handed beside the
/// per-subtask results. Pinned as EXACT text per outcome, because the words are the contract — the reduce is told to
/// state what landed and to name anything conflicted or withheld as NOT delivered, and it can only do that if the
/// sentence it reads says so.
///
/// <para>The honesty axis these pin: a non-Clean integration published NO branch (<c>IntegrationResult.IntegratedBranch</c>
/// is null on every such status, and the integrator resets its clone to base on an abort), so its applied count is a
/// trial count and never "landed". And a unit withheld before integration appears in no outcome at all — only the
/// withheld clause names it.</para>
/// </summary>
[Trait("Category", "Unit")]
public class RunIntegrationSummaryTests
{
    private static readonly IReadOnlyList<WithheldContribution> NoneWithheld = Array.Empty<WithheldContribution>();

    private static readonly WithheldContribution Flunked = new("agent#map#1", "acceptance Failed");

    [Fact]
    public void A_clean_integration_says_how_many_contributions_landed_and_where()
    {
        var result = IntegrationResult.Build(IntegrationStatus.Clean, "codespace/integration/run1", new[] { Applied("agent#map#0"), Applied("agent#map#1") });

        RunIntegrationSummary.ForResult(result, NoneWithheld).ShouldBe("Integration: 2 contribution(s) landed on codespace/integration/run1.");
    }

    [Fact]
    public void A_withheld_unit_is_named_after_the_outcome_even_when_the_candidate_is_clean()
    {
        var result = IntegrationResult.Build(IntegrationStatus.Clean, "codespace/integration/run1", new[] { Applied("agent#map#0") });
        var withheld = new[] { Flunked, new WithheldContribution("agent#map#3", "acceptance Waived") };

        RunIntegrationSummary.ForResult(result, withheld).ShouldBe(
            "Integration: 1 contribution(s) landed on codespace/integration/run1. Withheld before integration: agent#map#1 — acceptance Failed, agent#map#3 — acceptance Waived.");
    }

    [Fact]
    public void A_conflicted_set_says_nothing_landed_and_names_the_conflicted_contribution_with_the_branch_that_keeps_its_work()
    {
        var result = IntegrationResult.Build(IntegrationStatus.Conflicted, null, new[] { Applied("agent#map#0"), Conflicted("agent#map#1", "codespace/agent/b") }, "a contribution conflicted while integrating");

        RunIntegrationSummary.ForResult(result, NoneWithheld).ShouldBe(
            "Integration conflicted: no integrated branch was published, so none of this run's work landed on one (1 of 2 contribution(s) applied cleanly, but integration is all-or-nothing). "
            + "Conflicted and withheld from the integrated branch: agent#map#1 → codespace/agent/b.");
    }

    [Fact]
    public void A_conflict_is_named_before_a_bystander_the_set_merely_never_attempted()
    {
        // The shape LocalGitBranchIntegrator.ResolvedContribution.Skip() produces: no files of its own, and the set-level reason.
        var bystander = Conflicted("agent#map#0", "codespace/agent/a") with { Skipped = true, ConflictedFiles = Array.Empty<string>(), Reason = "not integrated — an earlier contribution conflicted" };
        var result = IntegrationResult.Build(IntegrationStatus.Conflicted, null, new[] { bystander, Conflicted("agent#map#1", "codespace/agent/b") }, "a contribution conflicted while integrating");

        RunIntegrationSummary.ForResult(result, NoneWithheld).ShouldEndWith(
            "agent#map#1 → codespace/agent/b, agent#map#0 → codespace/agent/a (not attempted).");
    }

    [Fact]
    public void A_contribution_with_no_branch_to_fall_back_on_is_named_as_having_none()
    {
        var unintegrable = new ContributionOutcome { Label = "agent#map#2", Disposition = ContributionDisposition.Unintegrable, Reason = "no patch and no branch (the agent's work was not captured)" };
        var result = IntegrationResult.Build(IntegrationStatus.Conflicted, null, new[] { unintegrable }, "a contribution could not be applied");

        RunIntegrationSummary.ForResult(result, NoneWithheld).ShouldEndWith("agent#map#2 → no branch to review.");
    }

    [Fact]
    public void A_conflicted_set_whose_contributions_all_applied_states_the_reason_the_publish_was_refused()
    {
        var result = IntegrationResult.Build(IntegrationStatus.Conflicted, null, new[] { Applied("agent#map#0"), Applied("agent#map#1") }, "remote integration branch advanced");

        RunIntegrationSummary.ForResult(result, NoneWithheld).ShouldBe(
            "Integration conflicted: no integrated branch was published, so none of this run's work landed on one (2 of 2 contribution(s) applied cleanly, but integration is all-or-nothing). "
            + "Reason: remote integration branch advanced.");
    }

    [Fact]
    public void A_conflicted_set_names_its_withheld_units_after_its_conflicts()
    {
        var result = IntegrationResult.Build(IntegrationStatus.Conflicted, null, new[] { Conflicted("agent#map#0", "codespace/agent/a") }, "a contribution conflicted while integrating");

        RunIntegrationSummary.ForResult(result, new[] { Flunked }).ShouldEndWith(
            "agent#map#0 → codespace/agent/a. Withheld before integration: agent#map#1 — acceptance Failed.");
    }

    [Fact]
    public void An_integration_that_found_nothing_to_land_says_so_with_the_integrators_reason()
    {
        var result = IntegrationResult.Build(IntegrationStatus.Empty, null, new[] { Applied("agent#map#0") }, "every contribution was a no-op — nothing to integrate");

        RunIntegrationSummary.ForResult(result, NoneWithheld).ShouldBe("Integration landed nothing: every contribution was a no-op — nothing to integrate.");
    }

    [Fact]
    public void A_status_the_integrator_does_not_yet_produce_still_reads_as_applied_counts_and_claims_no_branch()
    {
        var result = IntegrationResult.Build(IntegrationStatus.Partial, null, new[] { Applied("agent#map#0"), Conflicted("agent#map#1", "codespace/agent/b") });

        RunIntegrationSummary.ForResult(result, NoneWithheld).ShouldBe("Integration Partial: 1 of 2 contribution(s) applied.");
    }

    /// <summary>A skipped integration says why — and still names the units withheld before it, which is usually the reason nothing was integrable.</summary>
    [Theory]
    [InlineData(false, "Integration skipped: the run produced no integrable work for this repository.")]
    [InlineData(true, "Integration skipped: the run produced no integrable work for this repository. Withheld before integration: agent#map#1 — acceptance Failed.")]
    public void A_skipped_integration_says_why_and_names_what_was_withheld_before_it(bool anyWithheld, string expected)
    {
        var withheld = anyWithheld ? new[] { Flunked } : Array.Empty<WithheldContribution>();

        RunIntegrationSummary.ForSkipped("the run produced no integrable work for this repository", withheld).ShouldBe(expected);
    }

    // ─── The bound: a wide fan-out can never bloat the reduce prompt it is appended to ───

    [Fact]
    public void The_bound_is_two_thousand_characters()
    {
        RunIntegrationSummary.MaxChars.ShouldBe(2_000);
    }

    [Fact]
    public void A_conflict_list_past_the_bound_is_cut_with_an_ellipsis_and_keeps_its_head()
    {
        var outcomes = Enumerable.Range(0, 200).Select(i => Conflicted($"agent#map#{i}", $"codespace/agent/{new string('b', 40)}-{i}")).ToList();
        var result = IntegrationResult.Build(IntegrationStatus.Conflicted, null, outcomes, "a contribution conflicted while integrating");

        var summary = RunIntegrationSummary.ForResult(result, NoneWithheld);

        summary.Length.ShouldBe(2_000);
        summary.ShouldEndWith("…");
        summary.ShouldStartWith("Integration conflicted: no integrated branch was published");
    }

    [Fact]
    public void A_withheld_list_past_the_bound_is_cut_the_same_way_on_a_skipped_integration()
    {
        var withheld = Enumerable.Range(0, 200).Select(i => new WithheldContribution($"agent#map#{i}", "acceptance Failed")).ToList();

        var summary = RunIntegrationSummary.ForSkipped("the run produced no integrable work for this repository", withheld);

        summary.Length.ShouldBe(2_000);
        summary.ShouldEndWith("…");
    }

    [Fact]
    public void A_summary_exactly_at_the_bound_is_not_touched()
    {
        var reason = new string('r', RunIntegrationSummary.MaxChars - "Integration skipped: .".Length);

        var summary = RunIntegrationSummary.ForSkipped(reason, NoneWithheld);

        summary.Length.ShouldBe(2_000);
        summary.ShouldEndWith("r.");
    }

    /// <summary>A cut that lands between the halves of a surrogate pair would leave an unpaired high surrogate, which <c>System.Text.Json</c> refuses to encode — so the same arithmetic that protects the prompt would fail the node. The cut steps back over the pair instead.</summary>
    [Fact]
    public void A_cut_never_splits_a_surrogate_pair()
    {
        var filler = new string('r', 1_998 - "Integration skipped: ".Length);

        var summary = RunIntegrationSummary.ForSkipped(filler + "\U0001F600 and then some more text", NoneWithheld);

        summary.Length.ShouldBeLessThanOrEqualTo(2_000);
        Should.NotThrow(() => new UTF8Encoding(false, throwOnInvalidBytes: true).GetBytes(summary), "the bound must not leave half of a surrogate pair at the cut");
        summary.ShouldEndWith("…");
    }

    private static ContributionOutcome Applied(string label) => new() { Label = label, Disposition = ContributionDisposition.Applied };

    private static ContributionOutcome Conflicted(string label, string fallbackBranch) => new()
    {
        Label = label, Disposition = ContributionDisposition.Conflicted, ConflictedFiles = new[] { "shared.txt" }, FallbackBranch = fallbackBranch, Reason = "textual conflict applying the patch",
    };
}
