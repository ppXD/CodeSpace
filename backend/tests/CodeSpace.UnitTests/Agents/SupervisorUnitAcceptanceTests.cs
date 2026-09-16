using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Failures;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the PURE pieces of loopability slice 3 (per-unit objective acceptance). The rehydrate WIRING (grade-once,
/// positional join, fail-closed, repo resolution) is proven over real Postgres in
/// <c>SupervisorUnitAcceptanceFoldFlowTests</c>; this pins the decision logic in isolation: (1) the no-progress
/// EVIDENCE DISCOUNT — a WORK-classed rejection (<c>AcceptancePassed == false</c>, the check ran and the work failed
/// it) is NOT settled evidence even though it pushed a branch (the must-fix: without it an acceptance-failing retry
/// loop never trips the stall bound), while an INFRA-classed rejection (the check could not RUN — P0) KEEPS its
/// evidence; (2) <c>ReadPlanSubtasks</c> reads each subtask's authored acceptance off a plan payload; (3) the new
/// verdict fields are null-omitted so an ungraded unit serializes byte-identical.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorUnitAcceptanceTests
{
    private static SupervisorAgentResult BranchPushed(bool? acceptancePassed) => new()
    {
        AgentRunId = Guid.NewGuid(), Status = "Succeeded", ProducedBranch = "codespace/agent/x", AcceptancePassed = acceptancePassed,
    };

    // ── The no-progress evidence discount (the must-fix) ───────────────────────────────

    [Theory]
    [InlineData(null, true)]   // ungraded (no per-unit contract) — branch counts, byte-identical to pre-slice
    [InlineData(true, true)]   // graded PASS — verified work is evidence
    [InlineData(false, false)] // graded FAIL — a branch pushed but REJECTED is NOT progress (the discount)
    public void A_rejected_unit_is_discounted_from_settled_evidence_even_with_a_branch(bool? acceptancePassed, bool expectedEvidence)
    {
        SupervisorOutcome.HasSettledEvidence(new[] { BranchPushed(acceptancePassed) })
            .ShouldBe(expectedEvidence, "an objectively-rejected unit must not reset the no-progress streak; ungraded/passed are unchanged");
    }

    [Fact]
    public void A_self_reported_Succeeded_unit_that_failed_acceptance_is_not_evidence()
    {
        // Objective truth overrides the self-report: a unit can claim Succeeded yet fail its own definition-of-done.
        var result = new SupervisorAgentResult { AgentRunId = Guid.NewGuid(), Status = "Succeeded", ChangedFiles = new[] { "a.cs" }, AcceptancePassed = false };

        SupervisorOutcome.HasSettledEvidence(new[] { result }).ShouldBeFalse("a rejected unit is not progress even when its row says Succeeded with changed files");
    }

    [Fact]
    public void A_wave_with_one_accepted_unit_among_rejected_ones_still_has_evidence()
    {
        var results = new[] { BranchPushed(false), BranchPushed(false), BranchPushed(true) };

        SupervisorOutcome.HasSettledEvidence(results).ShouldBeTrue("at least one objectively-accepted unit is real forward progress");
    }

    // ── P0: an INFRA-classed rejection keeps its evidence (the check could not RUN — that is not failed work) ──

    [Theory]
    [InlineData("no-branch-or-repo")]          // publish failed with work present — the c5c6bba6 kill: correct work erased from progress
    [InlineData("grade-error: clone timeout")] // the grader's own failure
    [InlineData("no-rubric")]                  // half-authored spec — the check can never run as authored
    public void An_infra_classed_rejection_keeps_its_evidence(string detail)
    {
        var result = new SupervisorAgentResult { AgentRunId = Guid.NewGuid(), Status = "Succeeded", ChangedFiles = new[] { "report.md" }, AcceptancePassed = false, AcceptanceDetail = detail };

        SupervisorOutcome.HasSettledEvidence(new[] { result })
            .ShouldBeTrue("the discount exists to catch never-PASSING work, not never-RUNNING checks — discounting these marched a run with correct work into its no-progress kill");
    }

    [Fact]
    public void A_work_classed_rejection_stays_discounted()
    {
        // The original slice-3 purpose is untouched: a real check verdict (the work IS wrong) resets nothing.
        var result = new SupervisorAgentResult { AgentRunId = Guid.NewGuid(), Status = "Succeeded", ProducedBranch = "b", AcceptancePassed = false, AcceptanceDetail = "tests-failed-exit-1" };

        SupervisorOutcome.HasSettledEvidence(new[] { result }).ShouldBeFalse("a failing check on real work is still not progress");
    }

    [Fact]
    public void A_no_work_no_branch_rejection_has_no_evidence_to_keep()
    {
        // no-branch-or-repo with NO work is agent-fixable (do the work) — and there is nothing to count as evidence.
        var result = new SupervisorAgentResult { AgentRunId = Guid.NewGuid(), Status = "Failed", AcceptancePassed = false, AcceptanceDetail = "no-branch-or-repo" };

        SupervisorOutcome.HasSettledEvidence(new[] { result }).ShouldBeFalse();
    }

    // ── F1: an attempt THIS DEPLOYMENT ended never reaches a grade at all ──────────────

    [Fact]
    public void An_attempt_this_deployment_ended_is_graded_InfraUnknown_before_any_check_can_run()
    {
        // The shape a worker that could not broker the run's model credential leaves behind: nothing pushed, nothing
        // changed, no repo. Every grading arm past the short-circuit fails closed on that absence and hands back
        // "no-branch-or-repo", whose text rule reads GENUINE with no work present — so the unit's failure became
        // evidence that the MODEL could not do the work. Mutation: delete the short-circuit and this reads Failed.
        var ended = Compact(AgentRunStatus.Failed, FailureCodes.ModelCredentialBrokerUnavailable);

        var graded = SupervisorTurnService.InfraExitVerdict(ended).ShouldNotBeNull();

        graded.AcceptanceVerdict.ShouldBe(VerificationDisposition.InfraUnknown, "the typed verdict is what the quality reading classifies on");
        graded.AcceptanceDetail.ShouldBe("infra:model_credential_broker_unavailable", "the detail names the wall the attempt hit, verbatim");
        graded.AcceptancePassed.ShouldBe(false, "unchanged from the arms this replaces: work nothing verified must stay withheld from the reviewable head");
    }

    [Theory]
    [InlineData("non-zero-exit")]           // the agent ran and its command failed — a verdict about the work
    [InlineData("timed-out")]               // the agent ran out of wall clock — still its own attempt
    [InlineData("executor-error")]          // a throw that declared no failure identity — not ours to claim
    [InlineData("completed")]               // a success whose per-unit check must still run
    public void An_attempt_that_ran_is_left_for_the_grader(string exitReason)
    {
        // The falsifiable negative for the set: widen InfraExitReasons past what this deployment actually broke and
        // every ordinary failure stops buying the retries that could fix it.
        var ran = Compact(AgentRunStatus.Failed, exitReason);

        ran.InfraExitReason.ShouldBeNull("an ordinary exit reason never enters the durable compact — an untouched tape stays byte-identical");
        SupervisorTurnService.InfraExitVerdict(ran).ShouldBeNull("only an exit reason this codebase declares as its OWN infrastructure short-circuits the grade");
    }

    [Fact]
    public void An_attempt_that_wrote_no_result_at_all_is_left_for_the_grader()
    {
        // A cancelled / abandoned agent sets the ROW error with no result_jsonb, so there is no declared exit reason
        // to read. Silence is not a claim that this deployment ended it.
        var abandoned = SupervisorOutcome.ProjectCompact(Guid.NewGuid(), nameof(AgentRunStatus.Cancelled), "the worker never came back", resultJson: null);

        abandoned.InfraExitReason.ShouldBeNull();
        SupervisorTurnService.InfraExitVerdict(abandoned).ShouldBeNull();
    }

    [Fact]
    public void The_infra_exit_detail_prefix_is_pinned_and_reserved()
    {
        // Rule 8. The prefix is a DURABLE TAPE VALUE: it is written into acceptanceDetail and read back by
        // consumers that never see the producer, so renaming it silently re-classifies every tape already carrying
        // it — and reserving it is what lets IsInfraFailure key on a prefix at all.
        SupervisorAgentResult.InfraExitDetailPrefix.ShouldBe("infra:");

        // The INVENTORY of every other acceptance-detail this codebase composes, by producer. None may start with
        // the reserved prefix, or that composer's verdict would silently acquire this class's typed-only steers
        // (retry on a live worker) for a fault a retry reproduces forever.
        //   SupervisorAcceptanceGrader / the per-repo + captured arms: tests-passed, tests-failed-exit-N,
        //     tests-timed-out, setup-failed:, setup-timed-out, clone-failed:, grade-error:, oracle-restore-failed:,
        //     artifacts-present, schema-valid:, citations-resolve:, rubric …, accepted, not-applicable:, repo '…':
        //   SupervisorTurnService.Rehydrate: no-branch-or-repo, grade-skipped-budget-exhausted,
        //     "verification waived by a human co-sign"
        //   AgentAcceptanceContract: no-rubric, no-schema
        string[] everyOtherComposer =
        {
            "tests-passed", "tests-failed-exit-1", "tests-failed-exit-127", "tests-timed-out",
            "setup-failed: npm ci", "setup-timed-out", "clone-failed: auth", "grade-error: npm not found",
            "oracle-restore-failed: dirty tree", "no-rubric", "no-schema", "no-branch-or-repo",
            "grade-skipped-budget-exhausted", "artifacts-present", "schema-valid: 2 artifact(s)",
            "citations-resolve: 3 citation(s)", "rubric 0.90 ≥ 0.70", "accepted", "not-applicable: no changes were expected",
            "repo 'web': grade-error: boom", "verification waived by a human co-sign",
        };

        everyOtherComposer.Where(d => d.StartsWith(SupervisorAgentResult.InfraExitDetailPrefix, StringComparison.Ordinal))
            .ShouldBeEmpty("the prefix is reserved for details minted BEFORE any grader ran — a composer that collides with it inherits a steer written for a different failure");
    }

    [Theory]
    [InlineData(true)]    // the attempt had pushed work before the wall — already infra today, via no-branch-or-repo
    [InlineData(false)]   // it had not — the gap: today this reads as failed work
    public void The_infra_exit_detail_reads_as_infra_in_the_string_vocabulary_too(bool workPresent)
    {
        // The typed verdict is invisible to every reader that only ever sees the detail — the no-progress evidence
        // discount, the receipts, the decider's verdict line, the model-escalation trigger. Without this the fold
        // would REPLACE a detail those readers already classified as infra (work present) with one they do not.
        AgentAcceptanceContract.IsInfraFailure("infra:model_credential_broker_unavailable", workPresent)
            .ShouldBeTrue("a worker that went away is not evidence about the work in either direction");
    }

    [Fact]
    public void An_attempt_this_deployment_ended_keeps_the_work_it_had_already_produced_as_evidence()
    {
        // The no-progress budget, end to end over the production verdict: a unit whose attempt was killed by a
        // rolling restart AFTER it changed files must not have that work discounted, or a deploy marches the run
        // into its own stall bound. Mutation: drop the infra: arm from IsInfraFailure and this reddens.
        var ended = Compact(AgentRunStatus.Failed, FailureCodes.ModelCredentialBrokerUnavailable, changedFiles: new[] { "a.cs" });

        SupervisorOutcome.HasSettledEvidence(new[] { SupervisorTurnService.InfraExitVerdict(ended)! }).ShouldBeTrue();
    }

    /// <summary>
    /// One attempt as the tape actually records it: through the SAME <see cref="SupervisorOutcome.ProjectCompact"/>
    /// production folds a durable AgentRun row with, so a fixture can never carry an exit reason the projector would
    /// have dropped — the membership test lives there, and a hand-built compact would route around it.
    /// </summary>
    private static SupervisorAgentResult Compact(AgentRunStatus status, string exitReason, IReadOnlyList<string>? changedFiles = null)
    {
        var result = new AgentRunResult { Status = status, ExitReason = exitReason, ChangedFiles = changedFiles ?? Array.Empty<string>() };

        return SupervisorOutcome.ProjectCompact(Guid.NewGuid(), status.ToString(), rowError: null, JsonSerializer.Serialize(result, AgentJson.Options));
    }

    // ── ReadPlanSubtasks: the per-unit acceptance source ───────────────────────────────

    [Fact]
    public void ReadPlanSubtasks_reads_each_subtask_and_its_authored_acceptance()
    {
        const string planPayload = """
            {"goal":"g","subtasks":[
              {"id":"s1","title":"scaffold","instruction":"do"},
              {"id":"s2","title":"wire","instruction":"do","dependsOn":["s1"],"acceptance":{"command":["make","test"],"description":"green"}}
            ]}
            """;

        var subtasks = SupervisorOutcome.ReadPlanSubtasks(planPayload);

        subtasks.Count.ShouldBe(2);
        subtasks[0].Acceptance.ShouldBeNull("a subtask without a contract carries none");
        subtasks[1].DependsOn.ShouldBe(new[] { "s1" });
        subtasks[1].Acceptance!.Command.ShouldBe(new[] { "make", "test" });
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    public void ReadPlanSubtasks_is_empty_for_absent_or_malformed_payloads(string payload)
    {
        SupervisorOutcome.ReadPlanSubtasks(payload).ShouldBeEmpty();
    }

    // ── Byte-identity: the verdict fields are invisible when ungraded ──────────────────

    [Fact]
    public void An_ungraded_result_omits_both_verdict_fields()
    {
        var json = JsonSerializer.Serialize(new SupervisorAgentResult { AgentRunId = Guid.NewGuid(), Status = "Succeeded" }, AgentJson.Options);

        json.ShouldNotContain("acceptancePassed", Case.Insensitive, "a null verdict must be omitted — the durable agentResults bytes stay identical to pre-slice");
        json.ShouldNotContain("acceptanceDetail", Case.Insensitive);
    }

    [Fact]
    public void A_graded_result_round_trips_the_verdict()
    {
        var original = new SupervisorAgentResult { AgentRunId = Guid.NewGuid(), Status = "Succeeded", ProducedBranch = "b", AcceptancePassed = false, AcceptanceDetail = "tests-failed-exit-1" };

        var back = JsonSerializer.Deserialize<SupervisorAgentResult>(JsonSerializer.Serialize(original, AgentJson.Options), AgentJson.Options)!;

        back.AcceptancePassed.ShouldBe(false);
        back.AcceptanceDetail.ShouldBe("tests-failed-exit-1");
    }

    // ── The positional invariant the per-unit join depends on: crash-recovery re-park must re-derive agentRunIds in
    //    NUMERIC spawn-index order. The wait key's #{k} is non-zero-padded, so a lexicographic sort scrambles K≥11. ──

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(15)]
    public void SpawnIndexOf_parses_the_trailing_index_off_the_wait_key(int k)
    {
        SupervisorOutcome.SpawnIndexOf(SupervisorOutcome.AgentWaitKey("sup", turnNumber: 1, spawnIndex: k)).ShouldBe(k);
    }

    [Fact]
    public void SpawnIndexOf_sorts_a_malformed_key_last_rather_than_throwing()
    {
        SupervisorOutcome.SpawnIndexOf("sup#turn1#notanumber").ShouldBe(int.MaxValue);
        SupervisorOutcome.SpawnIndexOf("nohash").ShouldBe(int.MaxValue);
    }

    [Fact]
    public void Ordering_re_park_keys_by_spawn_index_preserves_numeric_order_for_K_over_ten()
    {
        // The 12 per-turn wait keys a K=12 spawn staged, in numeric order. A lexicographic sort would yield
        // #0,#1,#10,#11,#2,… — scrambling agentRunIds out of subtaskIds[i] order and corrupting the per-unit grade.
        var numeric = Enumerable.Range(0, 12).Select(k => SupervisorOutcome.AgentWaitKey("sup", 1, k)).ToList();
        var scrambled = numeric.OrderBy(key => key, StringComparer.Ordinal).ToList();

        scrambled.ShouldNotBe(numeric, "precondition: a lexicographic order genuinely differs at K≥11 (the test has teeth)");
        scrambled.OrderBy(SupervisorOutcome.SpawnIndexOf).ToList()
            .ShouldBe(numeric, "ordering the re-derived waits by parsed spawn index restores the authored subtaskIds[i] order");
    }
}
