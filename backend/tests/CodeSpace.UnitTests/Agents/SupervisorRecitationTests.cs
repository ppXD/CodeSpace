using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// The S8 recitation block — pure over the prior-decision tape: the NEWEST plan's items each joined (positionally)
/// to their LATEST covering spawn/retry result, rendered with live states + an explicit unfinished list at the
/// prompt tail. No plan ⇒ null ⇒ the prompt stays byte-identical.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SupervisorRecitationTests
{
    [Fact]
    public void No_plan_renders_nothing() =>
        SupervisorRecitation.Render(new[] { Prior(1, SupervisorDecisionKinds.AskHuman, "{}") }).ShouldBeNull();

    [Fact]
    public void Items_join_their_latest_attempt_and_the_unfinished_list_names_the_remaining_work()
    {
        var priors = new[]
        {
            Plan(1, ("s1", "First"), ("s2", "Second")),
            Spawn(2, subtaskIds: new[] { "s1", "s2" },
                Result("Succeeded", acceptancePassed: true),
                Result("Failed", error: "exit 1")),
        };

        var recitation = SupervisorRecitation.Render(priors)!;

        recitation.ShouldStartWith(SupervisorRecitation.Header);
        recitation.ShouldContain("- [s1] First: done (accepted)");
        recitation.ShouldContain("- [s2] Second: failed (exit 1)");
        recitation.ShouldContain("Unfinished: s2.");
    }

    [Fact]
    public void An_accepted_row_graded_on_the_candidates_own_file_under_test_says_so()
    {
        // The compact and the decider's verdict line must never give the weak brain contradictory framings of one
        // row, so both render the SAME clause off the SAME detail. "done (accepted)" alone reads as a protected
        // pass, which is precisely what a check running the candidate's own copy of the file under test is not.
        var priors = new[]
        {
            Plan(1, ("s1", "First")),
            Spawn(2, new[] { "s1" }, Result("Succeeded", acceptancePassed: true, acceptanceDetail: "tests-passed" + AcceptanceOracleProtection.SubjectDetailMarker + "solution.sh")),
        };

        SupervisorRecitation.Render(priors)!.ShouldContain("- [s1] First: done (accepted) — graded on the candidate's OWN solution.sh, not a protected judge");
    }

    [Fact]
    public void A_retry_supersedes_the_original_spawn()
    {
        var priors = new[]
        {
            Plan(1, ("s1", "First")),
            Spawn(2, new[] { "s1" }, Result("Failed", error: "flaked")),
            Spawn(3, new[] { "s1" }, Result("Succeeded", acceptancePassed: true)),   // the retry attempt
        };

        SupervisorRecitation.Render(priors)!.ShouldContain("- [s1] First: done (accepted)", customMessage: "the freshest attempt wins — exactly the rule the decider prompt marks");
    }

    [Fact]
    public void A_genuine_retry_decision_supersedes_the_original_spawn()
    {
        // Real bug: a retry decision's payload carries the plan-local id as a SINGULAR "subtaskId" field
        // (SupervisorRetryPayload), never the spawn payload's PLURAL "subtaskIds" array — FindCoveringDecision
        // unconditionally called ReadSpawnSubtaskIds, which always read empty for a genuine retry, silently
        // skipping it and leaving the recitation showing the STALE original-spawn state forever.
        var priors = new[]
        {
            Plan(1, ("s1", "First")),
            Spawn(2, new[] { "s1" }, Result("Failed", error: "flaked")),
            Retry(3, "s1", Result("Succeeded", acceptancePassed: true)),
        };

        var recitation = SupervisorRecitation.Render(priors)!;

        recitation.ShouldContain("- [s1] First: done (accepted)", customMessage: "a GENUINE retry decision (singular subtaskId payload) must supersede the failed original spawn");
        recitation.ShouldContain("Every plan item is finished");
    }

    [Fact]
    public void A_rejected_unit_recites_its_acceptance_detail_and_a_replan_supersedes_the_old_plan()
    {
        var priors = new[]
        {
            Plan(1, ("old", "Old item")),
            Plan(2, ("s1", "Fresh item")),
            Spawn(3, new[] { "s1" }, Result("Succeeded", acceptancePassed: false, acceptanceDetail: "rubric 0.50 < 1.00")),
        };

        var recitation = SupervisorRecitation.Render(priors)!;

        recitation.ShouldNotContain("Old item", customMessage: "a re-plan supersedes — the recitation restates the CURRENT plan only");
        recitation.ShouldContain("REJECTED by its acceptance check (rubric 0.50 < 1.00)");
        recitation.ShouldContain("Unfinished: s1.");
    }

    [Fact]
    public void The_authoring_lint_validates_the_effective_spec_not_the_superseded_one()
    {
        // B3: a human approved a replacement for the plan's broken judge-without-rubric — nagging the model about
        // the superseded bytes would tell it to re-plan a check that is already fixed.
        var brokenPlan = Prior(1, SupervisorDecisionKinds.Plan,
            """{"goal":"g","subtasks":[{"id":"s1","title":"First","instruction":"do","acceptance":{"command":["report.md"],"kind":"LlmJudge"}}]}""", "{}");

        SupervisorRecitation.Render(new[] { brokenPlan })!
            .ShouldContain("INVALID as authored", customMessage: "baseline: the broken spec is linted before any amendment");

        var fix = SupervisorAmendAcceptance.IntoAskHuman(new SupervisorAmendAcceptancePayload
        {
            SubtaskId = "s1", Reason = "judge has no rubric",
            Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "sh", "check.sh" } },
        });
        var approvedFix = Prior(2, SupervisorDecisionKinds.AskHuman, fix.PayloadJson!, """{"question":"q","answer":"approve"}""");

        SupervisorRecitation.Render(new[] { brokenPlan, approvedFix })!
            .ShouldNotContain("INVALID as authored", customMessage: "the lint reads the co-signed EFFECTIVE spec");
    }

    [Fact]
    public void An_outstanding_amendment_overrides_the_stale_rejected_recital()
    {
        // B6: reciting the dead oracle's REJECTED verdict after a human approved its replacement drove a live brain
        // to re-amend five times instead of retrying. The override names the one next action and keeps the subtask
        // unfinished.
        var amend = SupervisorAmendAcceptance.IntoAskHuman(new SupervisorAmendAcceptancePayload
        {
            SubtaskId = "s1", Reason = "the check invokes missing tooling",
            Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "sh", "check.sh" } },
        });

        var priors = new[]
        {
            Plan(1, ("s1", "First")),
            Spawn(2, new[] { "s1" }, Result("Succeeded", acceptancePassed: false, acceptanceDetail: "grade-error: npm not found")),
            Prior(3, SupervisorDecisionKinds.AskHuman, amend.PayloadJson!, """{"question":"q","answer":"approve"}"""),
        };

        var recitation = SupervisorRecitation.Render(priors)!;

        recitation.ShouldContain("AMENDED by an approved co-sign", customMessage: "the stale verdict is named stale");
        recitation.ShouldContain("RETRY this subtask", customMessage: "the one next action is spelled out");
        recitation.ShouldNotContain("REJECTED by its acceptance check", customMessage: "the dead oracle's verdict no longer drives the loop");
        recitation.ShouldContain("Unfinished: s1", customMessage: "the amended subtask stays on the unfinished list");
    }

    [Fact]
    public void A_discarded_amendment_recites_the_re_amend_instead_of_the_re_plan_that_lost_it()
    {
        // The same hand-off one re-plan later: a plan DISCARDS every approved amendment, so the unrunnable verdict is
        // live again — and the infra arm's stock copy ("re-plan the check") asks for exactly the move that destroyed
        // the repair, three lines under a results block forbidding it. One prompt must not carry both verbs.
        var amend = SupervisorAmendAcceptance.IntoAskHuman(new SupervisorAmendAcceptancePayload
        {
            SubtaskId = "s1", Reason = "the check invokes missing tooling",
            Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "sh", "check.sh" } },
        });

        var priors = new[]
        {
            Plan(1, ("s1", "First")),
            Spawn(2, new[] { "s1" }, Result("Succeeded", acceptancePassed: false, acceptanceDetail: "grade-error: npm not found")),
            Prior(3, SupervisorDecisionKinds.AskHuman, amend.PayloadJson!, """{"question":"q","answer":"approve"}"""),
            Plan(4, ("s1", "First")),
        };

        SupervisorAmendObligation.StandingFor(priors, "s1").ShouldBe(SupervisorAmendStanding.Discarded,
            "fixture check — the re-plan must really have eaten the co-sign, or this asserts the None arm under a new name");

        var recitation = SupervisorRecitation.Render(priors)!;

        recitation.ShouldContain("a re-plan already DISCARDED the co-signed repair", customMessage: "the recital names what the last plan cost");
        recitation.ShouldContain("propose 'amend_acceptance' again", customMessage: "…and the verb that re-anchors the repair to this plan");
        recitation.ShouldNotContain("re-plan the check", customMessage: "the stock infra copy asks for the move that discards the repair");
        recitation.ShouldContain("Unfinished: s1", customMessage: "a check that still cannot run leaves the unit unfinished");
    }

    [Fact]
    public void A_waived_unit_recites_as_waived_never_as_done()
    {
        // B2 (FATAL-1): "done" alone would feed the decider a waived unit as ordinary evidence.
        var priors = new[]
        {
            Plan(1, ("s1", "First")),
            Spawn(2, new[] { "s1" }, new { agentRunId = Guid.NewGuid(), status = "Succeeded", acceptanceVerdict = "waived" }),
        };

        var recitation = SupervisorRecitation.Render(priors)!;

        recitation.ShouldContain("verification WAIVED by a human", customMessage: "the recitation names the waive");
        recitation.ShouldNotContain("done (accepted)", customMessage: "WAIVED ≠ PASSED in the model-facing recital too");
    }

    [Fact]
    public void Staged_but_unfolded_reads_running_and_unstaged_reads_pending()
    {
        var priors = new[]
        {
            Plan(1, ("s1", "First"), ("s2", "Second")),
            Spawn(2, new[] { "s1" } /* no folded results yet */),
        };

        var recitation = SupervisorRecitation.Render(priors)!;

        recitation.ShouldContain("- [s1] First: running");
        recitation.ShouldContain("- [s2] Second: pending");
    }

    [Fact]
    public void An_all_finished_plan_steers_toward_merge_and_stop()
    {
        var priors = new[]
        {
            Plan(1, ("s1", "First")),
            Spawn(2, new[] { "s1" }, Result("Succeeded", acceptancePassed: true)),
        };

        SupervisorRecitation.Render(priors)!.ShouldContain("Every plan item is finished");
    }

    [Fact]
    public void An_under_claim_recites_that_the_check_actually_passed_despite_the_self_reported_failure()
    {
        // P4-1: the inverse of the rejected-unit case above — Status "Failed" with AcceptancePassed true. Previously
        // fell straight through to "failed (...)" with no signal the agent's own check disagreed with its self-report.
        var priors = new[]
        {
            Plan(1, ("s1", "First")),
            Spawn(2, new[] { "s1" }, Result("Failed", error: "gave up", acceptancePassed: true, acceptanceDetail: "tests-passed")),
        };

        var recitation = SupervisorRecitation.Render(priors)!;

        recitation.ShouldContain("reported failed, but its OWN acceptance check actually PASSED (tests-passed)");
        recitation.ShouldContain("do not retry, merge it");
        recitation.ShouldContain("Every plan item is finished", customMessage: "an under-claimed unit is objectively done — it must not stay on the unfinished list");
    }

    [Fact]
    public void An_infra_classed_rejection_recites_could_not_run_never_rejected()
    {
        // P0: the recitation and the decider's verdict line must give the SAME framing — a check that could not RUN
        // (publish failed, work present) is not a rejection of the work, and the recited steer is re-plan, not retry.
        var priors = new[]
        {
            Plan(1, ("s1", "Report")),
            Prior(2, SupervisorDecisionKinds.Spawn, JsonSerializer.Serialize(new { subtaskIds = new[] { "s1" } }, AgentJson.Options),
                JsonSerializer.Serialize(new { agentResults = new object[] { new { agentRunId = Guid.NewGuid(), status = "Succeeded", changedFiles = new[] { "report.md" }, acceptancePassed = false, acceptanceDetail = "no-branch-or-repo" } } }, AgentJson.Options)),
        };

        var recitation = SupervisorRecitation.Render(priors)!;

        recitation.ShouldContain("its check COULD NOT RUN (no-branch-or-repo)");
        recitation.ShouldContain("re-plan the check, do not retry the agent");
        recitation.ShouldNotContain("REJECTED", customMessage: "an unrunnable check must never read as a rejection of the work");
    }

    [Fact]
    public void A_half_authored_acceptance_spec_is_linted_in_the_recitation()
    {
        // P0: the free tier-0 authoring check the supervisor lane's plans never got — a judge without a rubric can
        // NEVER pass at grade time; recite the lint every turn until the model re-plans the check, instead of paying
        // a clone + a fail-closed verdict + a retry temptation.
        var priors = new[]
        {
            Prior(1, SupervisorDecisionKinds.Plan, JsonSerializer.Serialize(new
            {
                subtasks = new object[]
                {
                    new { id = "s1", title = "Report", instruction = "write it", acceptance = new { kind = "LlmJudge", command = new[] { "report.md" } } },
                    new { id = "s2", title = "Sound", instruction = "do it", acceptance = new { command = new[] { "dotnet", "test" } } },
                },
            }, AgentJson.Options)),
        };

        var recitation = SupervisorRecitation.Render(priors)!;

        recitation.ShouldContain("acceptance spec is INVALID as authored", customMessage: "the lint recites until fixed");
        recitation.ShouldContain("re-plan this item's check", customMessage: "the steer: fix the CHECK before spawning");
        recitation.ShouldContain("- [s2] Sound: pending");
        recitation.ShouldNotContain("[s2] Sound: pending ⚠", customMessage: "a valid spec lints nothing");
    }

    /// <summary>
    /// The lint and the re-plan exit on ONE line. <c>no-rubric</c> classifies INFRA
    /// (<see cref="AgentAcceptanceContract.IsInfraFailure(string?, bool)"/>), which is exactly the verdict shape
    /// <see cref="SupervisorReplanStanding"/> fires on — so a half-authored spec the model then RE-PLANNED without
    /// fixing rendered the withdrawal ("do not author another plan") and the lint's own stock steer ("re-plan this
    /// item's check") a few characters apart, and a model picks its verb off the copy. The DIAGNOSIS still recites;
    /// only its verb defers to the one reading that resolves the item against the whole tape.
    /// </summary>
    [Fact]
    public void A_linted_spec_whose_replan_exit_fired_defers_its_verb_to_that_exit()
    {
        var priors = new[]
        {
            JudgeWithoutARubric(1),
            Spawn(2, new[] { "s1" }, Result("Succeeded", acceptancePassed: false, acceptanceDetail: "no-rubric")),
            JudgeWithoutARubric(3),
        };

        SupervisorReplanStanding.ExitFor(priors, "s1").ShouldBe(SupervisorReplanExit.ToStaging,
            "fixture check — an exit must really have fired, or this asserts the untouched first-time copy under a new name");

        var recitation = SupervisorRecitation.Render(priors)!;

        recitation.ShouldContain("acceptance spec is INVALID as authored", customMessage: "the diagnosis is the only line that names why this check can never pass — it must survive");
        recitation.ShouldContain("take the exit its verdict names above, not another plan", customMessage: "…and its verb defers to the exit the results block one screen up already named");
        recitation.ShouldNotContain("re-plan this item's check", customMessage: "one recitation must not forbid another plan and demand one");
    }

    /// <summary>
    /// The other half of the same gate, and the reason it is not simply "the exit is not None": the results block
    /// substitutes its exit ramp on TWO of its three verdict arms, and a work rejection against a GREEN (or
    /// unmeasured) baseline is not one of them — that unit is steered at the RETRY, with no exit named anywhere.
    /// The lint deferring to "the exit its verdict names above" there points at a sentence no block rendered, and
    /// the one verb the item actually needs — re-plan the broken spec the newest generation just authored — goes
    /// unsaid.
    ///
    /// <para>The tape is the reachable shape: the verdict was graded under the FIRST generation's valid check (so a
    /// work-classed rejection is honest), and the RE-PLAN is what authored the rubric-less judge the lint fires
    /// on.</para>
    /// </summary>
    [Fact]
    public void A_linted_spec_whose_verdict_named_no_exit_keeps_its_own_re_plan_verb()
    {
        var priors = new[]
        {
            TestsPassCheck(1),
            Spawn(2, new[] { "s1" }, Result("Succeeded", acceptancePassed: false, acceptanceDetail: "tests-failed-exit-1")),
            JudgeWithoutARubric(3),
        };

        SupervisorReplanStanding.ExitFor(priors, "s1").ShouldBe(SupervisorReplanExit.ToStaging,
            "fixture check — an exit DID fire, so this pins the GATE and not the absence of an exit");

        var recitation = SupervisorRecitation.Render(priors)!;

        recitation.ShouldContain("done but REJECTED by its acceptance check", customMessage: "the check RAN and rejected the work — no exit is recited on this arm");
        recitation.ShouldContain("re-plan this item's check", customMessage: "…so the lint keeps the only verb it has, which is also the right one: the newest plan's spec is the broken thing");
        recitation.ShouldNotContain("take the exit its verdict names above", customMessage: "there is no exit above to take — the verdict block steers this unit at a retry");
    }

    /// <summary>A plan whose ONE item stakes a VALID <c>TestsPass</c> check — the generation a WORK-classed rejection can honestly be graded under, so a later re-plan can break the spec without the recorded verdict shape changing with it.</summary>
    private static SupervisorPriorDecision TestsPassCheck(int seq) =>
        Prior(seq, SupervisorDecisionKinds.Plan, JsonSerializer.Serialize(new
        {
            subtasks = new[] { new { id = "s1", title = "Report", instruction = "write it", acceptance = new { kind = "TestsPass", command = new[] { "dotnet", "test" } } } },
        }, AgentJson.Options));

    /// <summary>A plan whose ONE item stakes an <c>LlmJudge</c> check with no rubric — the half-authored spec that can never pass at grade time and grades <c>no-rubric</c>, so a tape can carry both the authoring lint and an infra-classed verdict for the same unit.</summary>
    private static SupervisorPriorDecision JudgeWithoutARubric(int seq) =>
        Prior(seq, SupervisorDecisionKinds.Plan, JsonSerializer.Serialize(new
        {
            subtasks = new[] { new { id = "s1", title = "Report", instruction = "write it", acceptance = new { kind = "LlmJudge", command = new[] { "report.md" } } } },
        }, AgentJson.Options));

    // ─── A2 (P4-2) tier escalation ──────────────────────────────────────────────

    [Fact]
    public void An_escalated_retry_shows_the_note_without_affecting_the_finished_gate()
    {
        var priors = new[]
        {
            Plan(1, ("s1", "First")),
            Spawn(2, new[] { "s1" }, Result("Failed", error: "flaked")),
            Retry(3, "s1", Result("Succeeded", acceptancePassed: true), escalatedTo: "claude-sonnet-4-5", escalatedFrom: "claude-haiku-4-5", reason: "the prior attempt's self-report contradicted its acceptance grade (over_claim)"),
        };

        var recitation = SupervisorRecitation.Render(priors)!;

        recitation.ShouldContain("- [s1] First: done (accepted) [escalated to claude-sonnet-4-5: the prior attempt's self-report contradicted its acceptance grade (over_claim)]");
        recitation.ShouldContain("Every plan item is finished", customMessage: "the escalation suffix must never break the EXACT done/done(accepted) finished-match");
    }

    [Fact]
    public void A_retry_whose_escalation_found_no_stronger_model_says_so_instead_of_going_silent()
    {
        // D3: the trigger fired, the pool held nothing above the prior tier. The recitation must say the retry ran
        // on the SAME model on purpose — a blank note reads as "nobody tried", which is the wrong steer.
        var priors = new[]
        {
            Plan(1, ("s1", "First")),
            Spawn(2, new[] { "s1" }, Result("Failed", error: "flaked")),
            Retry(3, "s1", Result("Failed", error: "still flaking"), escalatedTo: null, escalatedFrom: "claude-haiku-4-5", reason: "the prior attempt's self-report contradicted its acceptance grade (over_claim)"),
        };

        var recitation = SupervisorRecitation.Render(priors)!;

        recitation.ShouldContain("[no stronger model than claude-haiku-4-5 in the pool: the prior attempt's self-report contradicted its acceptance grade (over_claim)]");
        recitation.ShouldNotContain("[escalated to", customMessage: "nothing was escalated");
    }

    [Fact]
    public void A_non_escalated_retry_shows_no_escalation_note()
    {
        var priors = new[]
        {
            Plan(1, ("s1", "First")),
            Retry(3, "s1", Result("Succeeded", acceptancePassed: true)),
        };

        SupervisorRecitation.Render(priors)!.ShouldNotContain("[escalated");
    }

    // ─── fixtures ────────────────────────────────────────────────────────────

    private static SupervisorPriorDecision Retry(int seq, string subtaskId, object result, string? escalatedTo = null, string? escalatedFrom = null, string? reason = null) =>
        Prior(seq, SupervisorDecisionKinds.Retry,
            JsonSerializer.Serialize(new { subtaskId }, AgentJson.Options),
            JsonSerializer.Serialize(new
            {
                agentResults = new[] { result },
                // The REASON is what makes an escalation record exist — `to` is null on a no-op (the trigger fired, the pool had nothing stronger).
                escalation = reason is null ? null : (object)new { to = escalatedTo, from = escalatedFrom, reason },
            }, AgentJson.Options));

    private static SupervisorPriorDecision Plan(int seq, params (string Id, string Title)[] subtasks) =>
        Prior(seq, SupervisorDecisionKinds.Plan, JsonSerializer.Serialize(new
        {
            subtasks = subtasks.Select(s => new { id = s.Id, title = s.Title, instruction = $"do {s.Id}" }).ToArray(),
        }, AgentJson.Options));

    private static SupervisorPriorDecision Spawn(int seq, string[] subtaskIds, params object[] results) =>
        Prior(seq, SupervisorDecisionKinds.Spawn,
            JsonSerializer.Serialize(new { subtaskIds }, AgentJson.Options),
            results.Length == 0 ? null : JsonSerializer.Serialize(new { agentResults = results }, AgentJson.Options));

    private static object Result(string status, bool? acceptancePassed = null, string? error = null, string? acceptanceDetail = null) =>
        new { agentRunId = Guid.NewGuid(), status, error, acceptancePassed, acceptanceDetail };

    private static SupervisorPriorDecision Prior(int seq, string kind, string payloadJson, string? outcomeJson = null) => new()
    {
        Id = Guid.NewGuid(),
        Sequence = seq,
        Status = SupervisorDecisionStatus.Succeeded,
        DecisionKind = kind,
        PayloadJson = payloadJson,
        OutcomeJson = outcomeJson,
    };
}
