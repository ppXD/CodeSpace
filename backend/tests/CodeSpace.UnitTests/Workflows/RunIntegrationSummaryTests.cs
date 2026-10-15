using System.Text;
using System.Text.RegularExpressions;
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
/// <para>The honesty axes these pin. WHICH contributions landed, and which of them landed although their own subtask
/// failed — a partial nothing checked, or verified work whose run failed afterwards. A non-Clean integration published NO branch, so its applied count is a trial count and never "landed" — and
/// a set that applied cleanly but was not pushed says that instead of claiming a conflict. A unit withheld before
/// integration appears in no outcome, so only the withheld clause names it, and every account carries it. Every label
/// is written <c>subtask N (label)</c> so the reduce can place it. Fixtures are the shapes
/// <c>LocalGitBranchIntegrator</c> actually produces: one outcome per contribution, everything after the first
/// conflict marked Skipped, and the integrator's own reason strings.</para>
/// </summary>
[Trait("Category", "Unit")]
public class RunIntegrationSummaryTests
{
    private const string Branch = "codespace/integration/run1";

    private static readonly IReadOnlyList<WithheldContribution> NoneWithheld = Array.Empty<WithheldContribution>();

    private static readonly IReadOnlyList<LandedFailedSubtask> NoneLandedFailed = Array.Empty<LandedFailedSubtask>();

    private static readonly WithheldContribution Flunked = new("agent#map#1", "acceptance Failed");

    private static LandedFailedSubtask Partial(string label) => new(label, AcceptancePassed: false);

    private static LandedFailedSubtask FailedAfterPassing(string label) => new(label, AcceptancePassed: true);

    private static string Account(IntegrationResult result, IReadOnlyList<WithheldContribution>? withheld = null, IReadOnlyList<LandedFailedSubtask>? landedFailed = null, bool? reviewApproved = null) =>
        RunIntegrationSummary.ForResult(result, withheld ?? NoneWithheld, landedFailed ?? NoneLandedFailed, reviewApproved);

    // ─── Clean: which landed, which of those are partial, what was withheld ───

    [Fact]
    public void A_clean_integration_names_the_contributions_that_landed_and_where()
    {
        var result = IntegrationResult.Build(IntegrationStatus.Clean, Branch, new[] { Applied("agent#map#0"), Applied("agent#map#1") });

        Account(result).ShouldBe($"Integration: 2 contribution(s) landed on {Branch}: subtask 1 (agent#map#0), subtask 2 (agent#map#1).");
    }

    [Fact]
    public void A_label_outside_the_plan_map_shape_is_named_as_it_is_never_numbered()
    {
        var result = IntegrationResult.Build(IntegrationStatus.Clean, Branch, new[] { Applied("sup#sup#turn1") });

        Account(result).ShouldBe($"Integration: 1 contribution(s) landed on {Branch}: sup#sup#turn1.");
    }

    [Fact]
    public void A_contribution_that_landed_although_its_own_subtask_failed_is_flagged_partial_and_unverified()
    {
        var result = IntegrationResult.Build(IntegrationStatus.Clean, Branch, new[] { Applied("agent#map#0"), Applied("agent#map#1") });

        Account(result, landedFailed: new[] { Partial("agent#map#1") }).ShouldBe(
            $"Integration: 2 contribution(s) landed on {Branch}: subtask 1 (agent#map#0), subtask 2 (agent#map#1). "
            + "Landed although its subtask failed (partial, unverified): subtask 2 (agent#map#1).");
    }

    [Fact]
    public void A_contribution_whose_acceptance_passed_before_its_run_failed_is_flagged_as_failed_afterwards_not_as_partial()
    {
        var result = IntegrationResult.Build(IntegrationStatus.Clean, Branch, new[] { Applied("agent#map#0"), Applied("agent#map#1") });

        var summary = Account(result, landedFailed: new[] { FailedAfterPassing("agent#map#1") });

        summary.ShouldBe(
            $"Integration: 2 contribution(s) landed on {Branch}: subtask 1 (agent#map#0), subtask 2 (agent#map#1). "
            + "Landed although its subtask failed afterwards (acceptance passed): subtask 2 (agent#map#1).");
        summary.ShouldNotContain("unverified", customMessage: "its work WAS verified — calling it a partial nothing checked would understate what is on the branch");
    }

    [Fact]
    public void Both_kinds_of_failed_landing_are_stated_each_in_its_own_clause_the_unverified_ones_first()
    {
        var result = IntegrationResult.Build(IntegrationStatus.Clean, Branch, new[] { Applied("agent#map#0"), Applied("agent#map#1"), Applied("agent#map#2") });

        Account(result, landedFailed: new[] { FailedAfterPassing("agent#map#0"), Partial("agent#map#2") }).ShouldBe(
            $"Integration: 3 contribution(s) landed on {Branch}: subtask 1 (agent#map#0), subtask 2 (agent#map#1), subtask 3 (agent#map#2). "
            + "Landed although its subtask failed (partial, unverified): subtask 3 (agent#map#2). "
            + "Landed although its subtask failed afterwards (acceptance passed): subtask 1 (agent#map#0).");
    }

    [Fact]
    public void A_withheld_unit_is_named_after_the_outcome_even_when_the_candidate_is_clean()
    {
        var result = IntegrationResult.Build(IntegrationStatus.Clean, Branch, new[] { Applied("agent#map#0") });
        var withheld = new[] { Flunked, new WithheldContribution("agent#map#3", "output review did not approve it") };

        Account(result, withheld).ShouldBe(
            $"Integration: 1 contribution(s) landed on {Branch}: subtask 1 (agent#map#0). "
            + "Withheld before integration: subtask 2 (agent#map#1) — acceptance Failed, subtask 4 (agent#map#3) — output review did not approve it.");
    }

    /// <summary><c>Waived</c> is only ever stamped by the SUPERVISOR's amend-acceptance path, whose units carry a turn label — never a map label — so the supervisor-lane shape is what pins its words.</summary>
    [Fact]
    public void A_unit_the_supervisor_waived_is_named_beside_what_landed_with_its_turn_label_as_it_is()
    {
        var result = IntegrationResult.Build(IntegrationStatus.Clean, Branch, new[] { Applied("sup#sup#turn1") });
        var waived = new[] { new WithheldContribution("sup#sup#turn1", "acceptance waived by the supervisor (not delivered)") };

        Account(result, waived).ShouldBe(
            $"Integration: 1 contribution(s) landed on {Branch}: sup#sup#turn1. "
            + "Withheld before integration: sup#sup#turn1 — acceptance waived by the supervisor (not delivered).");
    }

    [Theory]
    [InlineData(true, "A reviewer approved the retry.")]
    [InlineData(false, "A reviewer rejected the retry.")]
    public void A_resumed_pass_states_the_reviewers_verdict_in_a_fixed_sentence(bool approved, string sentence)
    {
        var result = IntegrationResult.Build(IntegrationStatus.Clean, Branch, new[] { Applied("agent#map#0") });

        Account(result, reviewApproved: approved).ShouldBe($"Integration: 1 contribution(s) landed on {Branch}: subtask 1 (agent#map#0). {sentence}");
    }

    [Fact]
    public void The_review_sentence_follows_what_landed_and_its_qualifiers_and_precedes_what_was_withheld()
    {
        var result = IntegrationResult.Build(IntegrationStatus.Clean, Branch, new[] { Applied("agent#map#0"), Applied("agent#map#1") });

        Account(result, new[] { new WithheldContribution("agent#map#2", "acceptance Failed") }, new[] { Partial("agent#map#1") }, reviewApproved: true).ShouldBe(
            $"Integration: 2 contribution(s) landed on {Branch}: subtask 1 (agent#map#0), subtask 2 (agent#map#1). "
            + "Landed although its subtask failed (partial, unverified): subtask 2 (agent#map#1). "
            + "A reviewer approved the retry. "
            + "Withheld before integration: subtask 3 (agent#map#2) — acceptance Failed.");
    }

    [Fact]
    public void A_first_pass_says_nothing_about_a_review()
    {
        var result = IntegrationResult.Build(IntegrationStatus.Clean, Branch, new[] { Applied("agent#map#0") });

        Account(result, reviewApproved: null).ShouldNotContain("reviewer");
    }

    // ─── Conflicted: nothing landed, and what was held back is named with where its work is ───

    [Fact]
    public void A_conflicted_set_says_nothing_landed_and_names_what_conflicted_with_the_branch_that_keeps_its_work()
    {
        var result = IntegrationResult.Build(IntegrationStatus.Conflicted, null, new[] { Applied("agent#map#0"), Conflicted("agent#map#1", "codespace/agent/b") }, "a contribution conflicted while integrating");

        Account(result).ShouldBe(
            "Integration conflicted: no integrated branch was published, so none of this run's work landed on one (1 of 2 contribution(s) applied cleanly, but integration is all-or-nothing); "
            + "the work stays on the agents' own branches and patches. "
            + "Conflicted or held back: subtask 2 (agent#map#1) → codespace/agent/b.");
    }

    [Fact]
    public void A_conflict_is_named_before_a_bystander_the_set_merely_never_attempted()
    {
        // The shape LocalGitBranchIntegrator.ResolvedContribution.Skip() produces: no files of its own, and the set-level reason.
        var bystander = Conflicted("agent#map#0", "codespace/agent/a") with { Skipped = true, ConflictedFiles = Array.Empty<string>(), Reason = "not integrated — an earlier contribution conflicted" };
        var result = IntegrationResult.Build(IntegrationStatus.Conflicted, null, new[] { bystander, Conflicted("agent#map#1", "codespace/agent/b") }, "a contribution conflicted while integrating");

        Account(result).ShouldEndWith("subtask 2 (agent#map#1) → codespace/agent/b, subtask 1 (agent#map#0) → codespace/agent/a (not attempted).");
    }

    [Fact]
    public void A_contribution_with_no_branch_to_fall_back_on_is_named_as_having_none()
    {
        var unintegrable = new ContributionOutcome { Label = "agent#map#2", Disposition = ContributionDisposition.Unintegrable, Reason = "no patch and no branch (the agent's work was not captured)" };
        var result = IntegrationResult.Build(IntegrationStatus.Conflicted, null, new[] { unintegrable }, "a contribution could not be applied");

        Account(result).ShouldEndWith("subtask 3 (agent#map#2) → no branch to review.");
    }

    [Fact]
    public void A_conflicted_set_names_its_withheld_units_after_its_conflicts()
    {
        var result = IntegrationResult.Build(IntegrationStatus.Conflicted, null, new[] { Conflicted("agent#map#0", "codespace/agent/a") }, "a contribution conflicted while integrating");

        Account(result, new[] { Flunked }).ShouldEndWith(
            "Conflicted or held back: subtask 1 (agent#map#0) → codespace/agent/a. Withheld before integration: subtask 2 (agent#map#1) — acceptance Failed.");
    }

    [Fact]
    public void A_conflicted_set_never_flags_a_partial_as_having_landed()
    {
        var result = IntegrationResult.Build(IntegrationStatus.Conflicted, null, new[] { Applied("agent#map#0"), Conflicted("agent#map#1", "codespace/agent/b") }, "a contribution conflicted while integrating");

        Account(result, landedFailed: new[] { Partial("agent#map#0"), FailedAfterPassing("agent#map#0") }).ShouldNotContain("Landed although", customMessage: "nothing landed on a conflicted set — a failed subtask cannot have landed");
    }

    // ─── Clean but not pushed: nothing conflicted, so the account must not say so ───

    [Theory]
    [InlineData("remote integration branch advanced — refusing to clobber it")]
    [InlineData("integration was clean but NOT pushed — no write-capable credential was supplied")]
    public void A_set_that_applied_cleanly_but_was_not_pushed_says_that_and_not_that_it_conflicted(string reason)
    {
        // The two reasons LocalGitBranchIntegrator.PublishAsync returns; every contribution stays Applied.
        var result = IntegrationResult.Build(IntegrationStatus.Conflicted, null, new[] { Applied("agent#map#0"), Applied("agent#map#1") }, reason);

        var summary = Account(result);

        summary.ShouldBe($"Integration applied every contribution cleanly but published no branch ({reason}), so none of this run's work landed on one; the work stays on the agents' own branches and patches.");
        summary.ShouldNotContain("conflicted", Case.Insensitive);
        summary.ShouldNotContain("all-or-nothing");
    }

    [Fact]
    public void A_set_that_was_not_pushed_for_no_stated_reason_still_says_nothing_landed()
    {
        var result = IntegrationResult.Build(IntegrationStatus.Conflicted, null, new[] { Applied("agent#map#0") });

        Account(result).ShouldBe("Integration applied every contribution cleanly but published no branch, so none of this run's work landed on one; the work stays on the agents' own branches and patches.");
    }

    // ─── The other statuses ───

    [Fact]
    public void An_integration_that_found_nothing_to_land_says_so_with_the_integrators_reason()
    {
        var result = IntegrationResult.Build(IntegrationStatus.Empty, null, new[] { Applied("agent#map#0") }, "every contribution was a no-op — nothing to integrate");

        Account(result).ShouldBe("Integration landed nothing: every contribution was a no-op — nothing to integrate.");
    }

    [Fact]
    public void A_status_the_integrator_does_not_yet_produce_says_nothing_landed_and_where_the_work_stays()
    {
        var result = IntegrationResult.Build(IntegrationStatus.Partial, null, new[] { Applied("agent#map#0"), Conflicted("agent#map#1", "codespace/agent/b") });

        Account(result).ShouldBe("Integration Partial: 1 of 2 contribution(s) applied; no integrated branch was reported, so nothing landed on one — the work stays on the agents' own branches and patches.");
    }

    [Theory]
    [InlineData(false, false, "Integration skipped: the run produced no integrable work for this repository.")]
    [InlineData(false, true, "Integration skipped: the run produced no integrable work for this repository. Withheld before integration: subtask 2 (agent#map#1) — acceptance Failed.")]
    [InlineData(true, false, "Integration skipped: the produced work recorded no base revision to integrate from. Nothing landed on an integrated branch; the produced work stays on the agents' own branches and patches.")]
    public void A_skipped_integration_says_why_and_where_any_produced_work_stays(bool workRemains, bool anyWithheld, string expected)
    {
        var reason = workRemains ? "the produced work recorded no base revision to integrate from" : "the run produced no integrable work for this repository";

        RunIntegrationSummary.ForSkipped(reason, workRemains, anyWithheld ? new[] { Flunked } : NoneWithheld).ShouldBe(expected);
    }

    // ─── The bound: lists give way, prose does not, and the withheld clause is the last to give ───

    [Fact]
    public void The_bound_is_two_thousand_characters()
    {
        RunIntegrationSummary.MaxChars.ShouldBe(2_000);
    }

    [Fact]
    public void A_summary_exactly_at_the_bound_is_not_touched()
    {
        var lead = "Integration: 1 contribution(s) landed on b: ";
        var label = new string('x', RunIntegrationSummary.MaxChars - lead.Length - 1);

        var summary = Account(IntegrationResult.Build(IntegrationStatus.Clean, "b", new[] { Applied(label) }));

        summary.Length.ShouldBe(2_000);
        summary.ShouldBe(lead + label + ".");
    }

    [Fact]
    public void A_list_that_cannot_fit_even_one_entry_says_how_many_it_left_out()
    {
        var label = new string('x', RunIntegrationSummary.MaxChars);

        var summary = Account(IntegrationResult.Build(IntegrationStatus.Clean, "b", new[] { Applied(label) }));

        summary.ShouldBe("Integration: 1 contribution(s) landed on b: … and 1 more.");
    }

    /// <summary>The realistic wide shape: one outcome per contribution, the ones before the first conflict Applied, the conflict itself, and everything after it Skipped — with the real branch-name length. The real failure is first, so it is the entry that is always kept.</summary>
    [Fact]
    public void A_conflict_list_past_the_bound_is_cut_saying_how_many_it_left_out_and_where_the_rest_lives()
    {
        var result = WideConflict(contributions: 100, appliedBeforeTheConflict: 3);

        var summary = Account(result);
        var named = Regex.Matches(summary, "→").Count;
        var omitted = int.Parse(Regex.Match(summary, @"… and (\d+) more — see conflicts\[\]\.").Groups[1].Value);

        summary.Length.ShouldBeLessThanOrEqualTo(2_000);
        summary.ShouldStartWith("Integration conflicted: no integrated branch was published, so none of this run's work landed on one (3 of 100 contribution(s) applied cleanly");
        summary.ShouldContain("subtask 4 (agent#map#3) → codespace/agent/", customMessage: "the real conflict is named first, so it is never the entry that is cut");
        (named + omitted).ShouldBe(97, "every non-applied contribution is either named or counted in the cut — none silently dropped");
    }

    [Fact]
    public void The_withheld_clause_survives_a_conflict_list_that_fills_the_bound()
    {
        var withheld = new[] { Flunked, new WithheldContribution("agent#map#2", "acceptance Failed") };

        var summary = Account(WideConflict(contributions: 100, appliedBeforeTheConflict: 3), withheld);

        summary.Length.ShouldBeLessThanOrEqualTo(2_000);
        summary.ShouldEndWith(" Withheld before integration: subtask 2 (agent#map#1) — acceptance Failed, subtask 3 (agent#map#2) — acceptance Failed.",
            customMessage: "every account carries its withheld clause whole while it fits — the conflict list gives way first");
    }

    [Fact]
    public void Every_list_gives_way_before_the_withheld_clause_does()
    {
        var landed = Enumerable.Range(0, 150).Select(i => Applied($"agent#map#{i}")).ToList();
        var result = IntegrationResult.Build(IntegrationStatus.Clean, Branch, landed);
        var unverified = Enumerable.Range(0, 120).Select(i => Partial($"agent#map#{i}")).ToList();
        var withheld = Enumerable.Range(150, 12).Select(i => new WithheldContribution($"agent#map#{i}", "acceptance Failed")).ToList();

        var summary = Account(result, withheld, unverified);

        summary.Length.ShouldBeLessThanOrEqualTo(2_000);
        summary.ShouldEndWith($" Withheld before integration: {string.Join(", ", withheld.Select(w => $"subtask {int.Parse(w.Label.Split('#').Last()) + 1} ({w.Label}) — acceptance Failed"))}.");
        summary.ShouldContain("… and ", customMessage: "the landed and unverified lists were the ones cut");
        summary.ShouldContain(" Landed although its subtask failed (partial, unverified): subtask 1 (agent#map#0)", customMessage: "the unverified list still leads with its first entry — only its tail gave way");
    }

    /// <summary>The two qualifiers rank: a failed subtask whose acceptance PASSED is the least urgent thing the account says about the branch, so its list gives way before the partials nothing checked do.</summary>
    [Fact]
    public void The_verified_failures_give_way_before_the_unverified_partials_do()
    {
        var result = IntegrationResult.Build(IntegrationStatus.Clean, Branch, new[] { Applied("agent#map#0") });
        var unverified = Enumerable.Range(0, 30).Select(i => Partial($"agent#map#{i}")).ToList();
        var verified = Enumerable.Range(100, 120).Select(i => FailedAfterPassing($"agent#map#{i}")).ToList();

        var summary = Account(result, landedFailed: unverified.Concat(verified).ToList());

        summary.Length.ShouldBeLessThanOrEqualTo(2_000);
        summary.ShouldContain($" Landed although its subtask failed (partial, unverified): {string.Join(", ", unverified.Select(u => $"subtask {int.Parse(u.Label.Split('#').Last()) + 1} ({u.Label})"))}.",
            customMessage: "the unverified partials are whole — the verified list gave up the room instead");
        summary.ShouldMatch(@" Landed although its subtask failed afterwards \(acceptance passed\): subtask 101 \(agent#map#100\).*… and \d+ more\.$");
    }

    [Fact]
    public void A_withheld_list_that_alone_fills_the_bound_is_cut_with_its_own_count_and_pointer()
    {
        var withheld = Enumerable.Range(0, 200).Select(i => new WithheldContribution($"agent#map#{i}", "acceptance Failed")).ToList();

        var summary = RunIntegrationSummary.ForSkipped("the run produced no integrable work for this repository", workRemains: false, withheld);

        summary.Length.ShouldBeLessThanOrEqualTo(2_000);
        summary.ShouldStartWith("Integration skipped: the run produced no integrable work for this repository. Withheld before integration: subtask 1 (agent#map#0) — acceptance Failed");
        summary.ShouldMatch(@"… and \d+ more — see withheld\[\]\.$");
    }

    [Fact]
    public void An_integrators_long_reason_is_clipped_so_it_cannot_crowd_out_the_lists()
    {
        var result = IntegrationResult.Build(IntegrationStatus.Empty, null, new[] { Applied("agent#map#0") }, new string('r', 10_000));

        var summary = Account(result, new[] { Flunked });

        summary.Length.ShouldBeLessThanOrEqualTo(2_000);
        summary.ShouldEndWith(" Withheld before integration: subtask 2 (agent#map#1) — acceptance Failed.");
    }

    /// <summary>A clip that lands between the halves of a surrogate pair would leave an unpaired high surrogate, which <c>System.Text.Json</c> refuses to encode — so the same arithmetic that protects the prompt would fail the node. The clip steps back over the pair instead.</summary>
    [Fact]
    public void A_clip_never_splits_a_surrogate_pair()
    {
        var reason = new string('r', 298) + "\U0001F600 and then some more text";

        var summary = Account(IntegrationResult.Build(IntegrationStatus.Empty, null, new[] { Applied("agent#map#0") }, reason));

        Should.NotThrow(() => new UTF8Encoding(false, throwOnInvalidBytes: true).GetBytes(summary), "the clip must not leave half of a surrogate pair");
        summary.ShouldBe($"Integration landed nothing: {new string('r', 298)}….");
    }

    private static ContributionOutcome Applied(string label) => new() { Label = label, Disposition = ContributionDisposition.Applied };

    private static ContributionOutcome Conflicted(string label, string fallbackBranch) => new()
    {
        Label = label, Disposition = ContributionDisposition.Conflicted, ConflictedFiles = new[] { "shared.txt" }, FallbackBranch = fallbackBranch, Reason = "textual conflict applying the patch",
    };

    /// <summary>What the integrator returns when the set breaks partway: one outcome per contribution — the first <paramref name="appliedBeforeTheConflict"/> Applied, the next the real conflict, every one after it a Skipped bystander — each carrying its agent's real-length branch.</summary>
    private static IntegrationResult WideConflict(int contributions, int appliedBeforeTheConflict)
    {
        var outcomes = Enumerable.Range(0, contributions).Select(i => i switch
        {
            _ when i < appliedBeforeTheConflict => Applied($"agent#map#{i}"),
            _ when i == appliedBeforeTheConflict => Conflicted($"agent#map#{i}", AgentBranch(i)),
            _ => Conflicted($"agent#map#{i}", AgentBranch(i)) with { Skipped = true, ConflictedFiles = Array.Empty<string>(), Reason = "not integrated — an earlier contribution conflicted" },
        }).ToList();

        return IntegrationResult.Build(IntegrationStatus.Conflicted, null, outcomes, "a contribution conflicted while integrating");
    }

    private static string AgentBranch(int index) => $"codespace/agent/{index:x32}";
}
