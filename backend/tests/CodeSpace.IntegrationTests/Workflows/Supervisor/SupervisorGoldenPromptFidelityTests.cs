using System.Security.Cryptography;
using System.Text;
using CodeSpace.Core.Services.Completion;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// 🟢 Always-on (no model, no Postgres): the golden corpus's PROMPT is what the real-model gate actually measures,
/// so a fixture whose context drifts silently changes what that gate is testing. This pins the state-dependent
/// blocks per scenario.
///
/// <para>It exists because that drift already happened and cost real signal: the action mask reads the resolve cap
/// off the turn context, every scenario left it unset, and the lane default of ONE meant the mask told the model
/// "the resolve cap is spent — a further resolve FORCE-STOPS this run" inside <c>unverified-resolution</c>, whose
/// entire point is that the model should resolve again. The scenario stayed green only because its accepted set
/// also allowed Stop, so the corpus reported health while its teeth were gone.</para>
///
/// <para>These assertions are deliberately about the BLOCK's presence and arm, not its wording: the copy is pinned
/// by the decider's own unit tests, and duplicating it here would make prose edits a two-file chore for no extra
/// safety.</para>
/// </summary>
[Trait("Category", "Integration")]
public class SupervisorGoldenPromptFidelityTests
{
    /// <summary>
    /// Scenarios where resolve IS the move being measured — a live conflict with budget left. The mask must be
    /// ABSENT here, or the corpus asks for a move the same prompt forbids.
    /// </summary>
    private static readonly HashSet<string> ResolveAvailable = new(StringComparer.Ordinal)
    {
        "merge-conflict", "multi-file-conflict", "subset-conflict-across-three", "unverified-resolution",
    };

    /// <summary>
    /// Scenarios sitting ON the cap boundary — a recorded conflict with the budget spent. Masking resolve is
    /// CORRECT here even where resolve was never the expected answer: <c>verified-resolution</c>'s move is to
    /// accept the reconciliation, and telling the model that a further resolve would end the run does not compete
    /// with that. Its inclusion is deliberate, not incidental — the first draft of this table guessed otherwise.
    /// </summary>
    private static readonly HashSet<string> ResolveCapSpent = new(StringComparer.Ordinal)
    {
        "resolve-cap-spent", "verified-resolution",
    };

    /// <summary>
    /// The verbs the rendered prompt WITHHOLDS — parsed out of the mask block itself rather than off the whole
    /// prompt. The turn roster OFFERS its verbs in the same <c>- verb — …</c> shape three lines above, so a bare
    /// substring test would read an offered verb as a withheld one, and the block's mere presence no longer means
    /// "resolve is masked" now that <c>amend_acceptance</c> can be the only line in it.
    /// </summary>
    private static IReadOnlyList<string> WithheldInPrompt(string prompt) => VerbsUnder(prompt, SupervisorActionMask.Header);

    /// <summary>The verbs the rendered prompt OFFERS — the roster's own half, read the same way. Asserted BESIDE its withheld sibling wherever a verb's availability is the finding, so a change that offers and withholds the same verb fails on the pair rather than passing whichever half a test happened to look at.</summary>
    private static IReadOnlyList<string> OfferedInPrompt(string prompt) => VerbsUnder(prompt, SupervisorActionRoster.Header);

    /// <summary>The <c>- verb — …</c> lines directly under one block header, as verbs. Both halves render in that shape, three lines apart, so the header is the only thing that separates them and a bare substring test over the prompt reads one as the other.</summary>
    private static IReadOnlyList<string> VerbsUnder(string prompt, string header)
    {
        var start = prompt.IndexOf(header, StringComparison.Ordinal);

        if (start < 0) return [];

        return prompt[start..].Split('\n').Skip(1)
            .TakeWhile(line => line.StartsWith("- ", StringComparison.Ordinal))
            .Select(line => line[2..line.IndexOf(" — ", StringComparison.Ordinal)])
            .ToList();
    }

    [Fact]
    public void Every_scenario_renders_the_action_mask_arm_its_tape_implies()
    {
        foreach (var scenario in SupervisorDecisionGoldenScenarios.All)
        {
            var prompt = LlmSupervisorDecider.BuildUserPromptForTest(scenario.Context);
            var masked = WithheldInPrompt(prompt).Contains(SupervisorDecisionKinds.Resolve);

            if (ResolveCapSpent.Contains(scenario.Name))
            {
                masked.ShouldBeTrue($"'{scenario.Name}' sits on the resolve cap — the model must be told another resolve would end the run");
                prompt.ShouldContain("resolve cap is spent", Case.Insensitive, $"'{scenario.Name}' must render the CAP arm, not the no-conflict arm");
            }
            else if (ResolveAvailable.Contains(scenario.Name))
            {
                masked.ShouldBeFalse($"'{scenario.Name}' records a live conflict with budget left, so resolve is genuinely available — a mask here would contradict the move the scenario is measuring");
            }
            else
            {
                masked.ShouldBeTrue($"'{scenario.Name}' records no conflict, so a resolve would no-op — the mask must say so");
                prompt.ShouldContain("nothing to reconcile", Case.Insensitive, $"'{scenario.Name}' must render the NO-CONFLICT arm");
            }
        }
    }

    [Fact]
    public void The_resolution_verdict_never_contradicts_the_action_mask()
    {
        // The defect this pins is the shape of the one that shipped: the mask said a further resolve would
        // force-stop the run while the resolution verdict, in the SAME prompt, told the model to issue one.
        // Whichever text the model reads last, the advice has to be the same advice.
        foreach (var scenario in SupervisorDecisionGoldenScenarios.All)
        {
            var prompt = LlmSupervisorDecider.BuildUserPromptForTest(scenario.Context);

            if (!prompt.Contains("resolve cap is spent", StringComparison.OrdinalIgnoreCase)) continue;

            prompt.ShouldNotContain("Issue another 'resolve'", Case.Sensitive,
                $"'{scenario.Name}' offers a resolve the same prompt says would force-stop the run");
        }
    }

    [Fact]
    public void A_non_conflict_integration_failure_prompt_exposes_a_reachable_repair_instead_of_a_dead_end()
    {
        var merge = new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(),
            Sequence = 2,
            DecisionKind = SupervisorDecisionKinds.Merge,
            Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = "{}",
            OutcomeJson = """{"merged":[],"count":0,"integration":{"status":"Failed","reason":"repository hook rejected the integrated tree","outcomes":[]}}""",
        };
        var context = new SupervisorTurnContext { Goal = "ship the coordinated change", TurnNumber = 3, PriorDecisions = [merge] };

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(context);

        prompt.ShouldContain("retry the affected planned unit", Case.Insensitive);
        prompt.ShouldContain("spawn a focused fix-up unit", Case.Insensitive);
        prompt.ShouldContain("ask_human", Case.Sensitive);
        prompt.ShouldNotContain("choose 'resolve'", Case.Insensitive, "the action mask correctly withholds resolve when no conflict was recorded");
        WithheldInPrompt(prompt).ShouldContain(SupervisorDecisionKinds.Resolve);
        OfferedInPrompt(prompt).ShouldNotContain(SupervisorDecisionKinds.Resolve);
    }

    /// <summary>
    /// Arc-3 item 4.3's residual, one screen before the co-sign loop it feeds: <c>first-infra-failure</c> is graded
    /// on <c>plan</c> or <c>ask_human</c> — never <c>amend_acceptance</c>, even though the menu genuinely offers it.
    /// The FIRST-time infra copy is pinned at the unit level as deliberately unmoving
    /// (<c>SupervisorDeciderTests.An_unrun_re_plan_is_steered_at_the_staging_it_is_waiting_for</c> and its sibling),
    /// so this is the golden-corpus half of that same fact: the menu and the steer must keep disagreeing on
    /// PREFERENCE without disagreeing on AVAILABILITY, or a live model reading both would be reading a
    /// contradiction rather than a judgement call.
    /// </summary>
    [Fact]
    public void The_first_infra_failure_is_steered_at_the_first_time_copy_while_the_menu_still_offers_the_repair()
    {
        var scenario = SupervisorDecisionGoldenScenarios.All.Single(s => s.Name == "first-infra-failure");
        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(scenario.Context);

        prompt.ShouldContain(LlmSupervisorDecider.ReplanThisItemWithASatisfiableCheck, Case.Sensitive,
            "the FIRST time a check comes back unrunnable, the un-amended first-time copy must render byte-identically to every other tape that reaches it");
        prompt.ShouldNotContain("Propose 'amend_acceptance'", Case.Sensitive,
            "no re-plan or co-sign is on this tape yet, so the steer must not (yet) prefer the human-gated repair over the free self-service one");

        OfferedInPrompt(prompt).ShouldContain(SupervisorDecisionKinds.AmendAcceptance,
            "the precondition never required a prior re-plan, only a graded infra-classed failure — so the menu already offers the verb the steer does not (yet) prefer");

        SupervisorMergeContributors.Resolve(scenario.Context.PriorDecisions).AgentRunIds
            .ShouldBeEmpty("both units are unrunnable — a mergeable sibling would make 'merge' a live decision-eval answer against a prompt that truthfully offers it (the amended-oracle-discarded-by-replan lesson), and this golden must leave one right move");
    }

    /// <summary>
    /// The sibling contradiction, one arc later: <c>amended-oracle-awaiting-retry</c> is graded on <c>retry</c>, so
    /// its prompt must offer the retry that CONSUMES the human's co-sign and must not offer the re-plan that
    /// DISCARDS it. Run 34066916864 is what a prompt saying both looks like — eight accepted plans, nothing spawned,
    /// a no-progress force-stop with no integrated head. Derived from the decider's own steer rather than restated,
    /// so a reword stays a one-file change.
    /// </summary>
    [Fact]
    public void The_cosigned_scenario_is_steered_at_the_retry_that_consumes_the_cosign()
    {
        var scenario = SupervisorDecisionGoldenScenarios.All.Single(s => s.Name == "amended-oracle-awaiting-retry");
        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(scenario.Context);

        SupervisorAmendObligation.FirstOutstanding(scenario.Context.PriorDecisions)
            .ShouldBe("s2", "the fixture must really carry an unconsumed co-sign, or the scenario measures nothing it claims to");

        prompt.ShouldContain(LlmSupervisorDecider.InfraSteerFor(SupervisorAmendStanding.AwaitingRetry, SupervisorReplanExit.None), Case.Sensitive,
            "the unit's own verdict line must steer at the retry its accepted set demands");
        prompt.ShouldNotContain("Re-plan this item", Case.Insensitive,
            "a scenario graded on 'retry' whose prompt asks for a re-plan measures obedience to a contradiction, not judgement");
    }

    /// <summary>
    /// The same bond one turn further on: <c>amended-oracle-discarded-by-replan</c> is graded on re-proposing the
    /// amendment, so its prompt must say the re-plan already ate the co-sign and must NOT ask for another plan —
    /// the arm the first cut of this fix fell through on, and the one the observed <c>plan×8</c> lived in. Derived
    /// from the decider's own steer rather than restated, so a reword stays a one-file change.
    ///
    /// <para>The steer is only half of what makes this a golden. Both OTHER places the prompt speaks about the same
    /// unit have to agree with it: the plan-state recitation (whose infra arm otherwise recites "re-plan the check"
    /// — the move the verdict one screen above just forbade), and the carry-over line, which truthfully offers
    /// "'merge' will include them" for any unit the tape left mergeable.</para>
    /// </summary>
    [Fact]
    public void The_discarded_cosign_scenario_is_steered_back_at_the_amendment_never_at_another_plan()
    {
        var scenario = SupervisorDecisionGoldenScenarios.All.Single(s => s.Name == "amended-oracle-discarded-by-replan");
        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(scenario.Context);

        foreach (var subtaskId in new[] { "s1", "s2" })
            SupervisorAmendObligation.StandingFor(scenario.Context.PriorDecisions, subtaskId).ShouldBe(SupervisorAmendStanding.Discarded,
                $"'{subtaskId}' must really carry a co-sign a later plan discarded, or the scenario measures nothing it claims to");

        prompt.ShouldContain(LlmSupervisorDecider.InfraSteerFor(SupervisorAmendStanding.Discarded, SupervisorReplanExit.None), Case.Sensitive,
            "the unit's own verdict line must steer at the verb its accepted set demands");
        prompt.ShouldContain(LlmSupervisorDecider.ReplanDiscardsTheCosign, Case.Sensitive,
            "…and the prompt must state, once, why the plan it just refused is the move that lost the repair");
        prompt.ShouldNotContain("Re-plan this item", Case.Insensitive,
            "a scenario graded on re-proposing the amendment whose prompt asks for a re-plan is the pre-fix prompt with a new name");
        prompt.ShouldNotContain("re-plan the check", Case.Insensitive,
            "the plan-state recitation asks for the re-plan the verdict block forbids — one prompt, two verbs, and the model picks whichever it read last");
        prompt.ShouldNotContain("OUTSTANDING ORACLE AMENDMENT", Case.Sensitive,
            "a discarded amendment owes no retry — the banner is outstanding-only, and this reading is steer-only");
    }

    /// <summary>
    /// The same bond with no human in it: <c>re-plan-left-the-verdict-unchanged</c> is graded on the amendment or a
    /// human ruling, so its prompt must say a re-plan already left the verdict where it found it and must NOT ask
    /// for another plan — in the results block OR in the plan-state recitation, which otherwise recites "re-plan the
    /// check" one screen from the steer that withdrew it.
    ///
    /// <para>This is the tape the ~25-40% attractor actually ran on: no co-sign, so every amended steer was
    /// inapplicable and the un-amended one re-rendered verbatim after each of six plans (runs 34104701023 and
    /// 34101026801 attempt 2). Derived from the decider's own ramp rather than restated, so a reword stays a
    /// one-file change.</para>
    /// </summary>
    [Fact]
    public void The_re_planned_scenario_is_steered_off_the_plan_it_already_spent()
    {
        var scenario = SupervisorDecisionGoldenScenarios.All.Single(s => s.Name == "re-plan-left-the-verdict-unchanged");

        foreach (var subtaskId in new[] { "s1", "s2" })
        {
            SupervisorAmendObligation.StandingFor(scenario.Context.PriorDecisions, subtaskId).ShouldBe(SupervisorAmendStanding.None,
                $"'{subtaskId}' must carry NO co-sign, or this scenario is the discarded one with a different name and measures the arm that already had a ramp");
            SupervisorReplanStanding.ExitFor(scenario.Context.PriorDecisions, subtaskId).ShouldBe(SupervisorReplanExit.ToAmendment,
                $"'{subtaskId}' must really have had a re-plan spent on it without its verdict moving, or the scenario measures nothing it claims to");
        }

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(scenario.Context);

        prompt.ShouldContain(LlmSupervisorDecider.ReplanExitRampFor(SupervisorReplanExit.ToAmendment)!, Case.Sensitive,
            "the unit's own verdict line must steer at the verbs its accepted set demands");
        prompt.ShouldNotContain("Re-plan this item", Case.Insensitive,
            "a scenario graded on amend/ask whose prompt asks for a re-plan is the pre-fix prompt with a new name");
        prompt.ShouldNotContain("re-plan the check", Case.Insensitive,
            "…and the plan-state recitation must not ask for it either — one prompt, two verbs, and the model picks whichever it read last");
        prompt.ShouldNotContain(LlmSupervisorDecider.ReplanDiscardsTheCosign, Case.Sensitive,
            "no human has co-signed anything on this tape, so there is no amendment a plan could discard — saying otherwise is a rule the model cannot act on");

        OfferedInPrompt(prompt).ShouldContain(SupervisorDecisionKinds.AmendAcceptance, "the menu offers the verb the steer names");
        WithheldInPrompt(prompt).ShouldNotContain(SupervisorDecisionKinds.AmendAcceptance, "…and never in the same breath");
        SupervisorMergeContributors.Resolve(scenario.Context.PriorDecisions).AgentRunIds
            .ShouldBeEmpty("a mergeable contributor makes 'merge' a defensible answer, and the accepted set does not admit it");
    }

    /// <summary>
    /// Why <c>amended-oracle-discarded-by-replan</c> holds ONE right move: nothing on its tape is mergeable. The
    /// first cut shipped it with a clean sibling unit, so the recitation truthfully told the model
    /// "1 succeeded result(s) … 'merge' will include them" — and the live decision eval answered <c>merge</c> on
    /// BOTH the gating and the informational wire, from a prompt that genuinely admitted it. Two independent wires
    /// agreeing is the corpus's own signal that a scenario, not a model, is wrong; the answer key was left alone and
    /// the fixture lost its mergeable unit. Asserted off <see cref="SupervisorMergeContributors"/> — the same
    /// selection the merge executor folds — so this cannot pass against a prompt line the merge would not honour.
    /// </summary>
    [Fact]
    public void The_discarded_cosign_scenario_leaves_nothing_a_merge_could_fold()
    {
        var scenario = SupervisorDecisionGoldenScenarios.All.Single(s => s.Name == "amended-oracle-discarded-by-replan");
        var selection = SupervisorMergeContributors.Resolve(scenario.Context.PriorDecisions);

        selection.AgentRunIds.ShouldBeEmpty("a mergeable contributor makes 'merge' a defensible answer, and the accepted set does not admit it");
        selection.CarriedOverFromEarlierGenerations.ShouldBe(0, "a stranded-but-mergeable result is the same affordance one generation back");

        LlmSupervisorDecider.BuildUserPromptForTest(scenario.Context)
            .ShouldNotContain("'merge' will include them", Case.Sensitive,
                "the carry-over line offers the model a verb this scenario grades as wrong — and it is offering it truthfully, which is the fixture's bug, not the line's");
    }

    /// <summary>
    /// The turn roster's <c>amend_acceptance</c> arm on the two co-sign scenarios — now ONE answer per tape, across
    /// the menu, the gate and the steer.
    ///
    /// <para><c>amended-oracle-awaiting-retry</c>: a co-sign is outstanding, the precondition refuses a second
    /// amend, the roster withholds the verb, the banner says RETRY, and the accepted set is {retry}.</para>
    ///
    /// <para><c>amended-oracle-discarded-by-replan</c>: the accepted set names <c>amend_acceptance</c>,
    /// <see cref="LlmSupervisorDecider.InfraSteerFor"/>'s Discarded arm tells the model to propose one, and
    /// <see cref="SupervisorAmendPrecondition"/> now ADMITS it — so the roster offers it. This test previously
    /// pinned the opposite as a named CONTRADICTION: the gate read its graded evidence through
    /// <see cref="SupervisorPlanWindow"/>, the re-plan this scenario is built around closes that window over the
    /// attempt which produced the evidence, and the arm answered "has never been attempted" while the gate's own
    /// FIRST arm, <see cref="SupervisorAmendObligation.StandingFor"/>, the steer and the answer key all read the
    /// whole tape. The roster then honestly reported "unavailable" one screen under a steer saying "propose
    /// amend_acceptance", and the decision eval's model answered with a third verb — <c>merge</c>, run
    /// 34085079257 at 24/25. The gate widened, which is the arc's own design (a
    /// <see cref="SupervisorAmendStanding.Discarded"/> repair is meant to be re-proposable, and re-anchoring the
    /// repaired check to the NEW plan is what the verb does); the scenario's accepted set never moved.</para>
    ///
    /// <para>Both halves of the roster are asserted on the discarded tape, so a regression that re-withholds the
    /// verb — or offers and withholds it at once — fails here rather than in a real-model score.</para>
    /// </summary>
    [Fact]
    public void The_cosign_pair_gets_the_amend_arm_its_own_precondition_agrees_with()
    {
        var awaitingRetry = SupervisorDecisionGoldenScenarios.All.Single(s => s.Name == "amended-oracle-awaiting-retry");
        var discarded = SupervisorDecisionGoldenScenarios.All.Single(s => s.Name == "amended-oracle-discarded-by-replan");

        WithheldInPrompt(LlmSupervisorDecider.BuildUserPromptForTest(awaitingRetry.Context))
            .ShouldContain(SupervisorDecisionKinds.AmendAcceptance, "a co-sign is outstanding here, so the precondition refuses a second amend — offering it invites the amend×5 loop of run 34066916864");
        SupervisorAmendPrecondition.AnyAmendableUnit(awaitingRetry.Context)
            .ShouldBeFalse("widening the evidence SCOPE must not reach past the outstanding-co-sign arm — that guard is what makes the amend×5 loop unreachable");

        // One answer per tape. Every surface is asserted, so none of them can move alone.
        discarded.AcceptedKinds.ShouldContain(SupervisorDecisionKinds.AmendAcceptance,
            "the scenario grades the verb, and the Discarded steer sends the model at it");
        SupervisorAmendPrecondition.Reject(discarded.Context, new SupervisorAmendAcceptancePayload { SubtaskId = "s1", Waive = true, Reason = "the check could not run" })
            .ShouldBeNull("…and the server's own gate admits it: the re-plan discarded the repair, it did not un-grade the attempt that warrants one");
        SupervisorAmendPrecondition.AnyAmendableUnit(discarded.Context)
            .ShouldBeTrue("…so the roster's availability reader must answer the same as the arm above, off the same evidence");

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(discarded.Context);

        OfferedInPrompt(prompt).ShouldContain(SupervisorDecisionKinds.AmendAcceptance,
            "the menu must OFFER the verb the steer one screen below sends the model at — told to propose an amendment under a roster reporting it unavailable, the eval's model picked a third verb (34085079257, 24/25)");
        WithheldInPrompt(prompt).ShouldNotContain(SupervisorDecisionKinds.AmendAcceptance,
            "…and the withheld half must not name it in the same breath — that is the two-rosters defect this block was built to end");
    }

    [Fact]
    public void An_answered_ask_human_reaches_the_model_as_the_answer_never_as_the_wait_token()
    {
        // D6: these tapes are built by the PRODUCTION card builders and carry a real askHumanToken. The prompt used
        // to render the whole outcome jsonb, so the brain read a server correlation key it can do nothing with, and
        // the human's actual words arrived wrapped in json. The answer is the fact the next decision turns on.
        var answered = SupervisorDecisionGoldenScenarios.All
            .Where(s => s.Context.PriorDecisions.Any(d => d.DecisionKind == SupervisorDecisionKinds.AskHuman && SupervisorOutcome.ReadAskHumanAnswer(d.OutcomeJson) is not null))
            .ToList();

        answered.ShouldNotBeEmpty("the corpus must keep at least one answered-ask tape, or this pins nothing");

        foreach (var scenario in answered)
        {
            var prompt = LlmSupervisorDecider.BuildUserPromptForTest(scenario.Context);

            foreach (var decision in scenario.Context.PriorDecisions.Where(d => d.DecisionKind == SupervisorDecisionKinds.AskHuman))
            {
                if (SupervisorOutcome.ReadHumanWaitToken(decision.OutcomeJson) is { } token)
                    prompt.ShouldNotContain(token, Case.Sensitive, $"'{scenario.Name}' leaks the internal wait token into the model-facing prompt");

                if (SupervisorOutcome.ReadAskHumanAnswer(decision.OutcomeJson) is { } answer)
                    prompt.ShouldContain(answer, Case.Sensitive, $"'{scenario.Name}' must show the model what the human actually answered");
            }
        }
    }

    [Fact]
    public void Every_planned_scenario_recites_its_plan_state_the_way_production_does()
    {
        // The block that names which subtask is done, which failed, and which is still unfinished. It was absent from
        // EVERY golden prompt because the fixture serialized subtask IDs as bare strings where the production payload
        // holds objects — the read threw, was swallowed, and returned empty. The scenarios graded on naming the failed
        // subtask were therefore measuring positional inference off a raw payload dump. Nothing failed, because a
        // missing block cannot fail; it can only quietly make the gate easier than production.
        foreach (var scenario in SupervisorDecisionGoldenScenarios.All)
        {
            var planned = scenario.Context.PriorDecisions.Where(d => d.DecisionKind == SupervisorDecisionKinds.Plan).ToList();

            if (planned.Count == 0) continue;   // 'first-turn' has an empty tape by design

            SupervisorOutcome.ReadPlanSubtasks(planned[^1].PayloadJson).Count
                .ShouldBeGreaterThan(0, $"'{scenario.Name}' has a plan on its tape whose payload does not parse into subtasks — every downstream recitation silently renders nothing");

            LlmSupervisorDecider.BuildUserPromptForTest(scenario.Context)
                .ShouldContain("CURRENT PLAN STATE", Case.Sensitive, $"'{scenario.Name}' must show the model the same plan state production would");
        }
    }

    [Fact]
    public void A_scenario_graded_on_naming_a_subtask_shows_the_model_that_subtask_by_id()
    {
        // The sharpest case: three scenarios are scored on whether the model targets the RIGHT failed subtask, and
        // one of them ('mixed-results') came back from a live run as "retry targeted ''". A model cannot name what
        // the prompt never states.
        // The target lives inside each scenario's PayloadCheck closure, so it is restated here. Kept deliberately
        // small: if a fourth retry-graded scenario is added and not listed, the sibling fact above still requires it
        // to recite a plan state at all.
        var graded = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mixed-results"] = "s2",
            ["three-subtask-partial-failure"] = "s2",
            ["five-subtask-middle-failed"] = "s3",
            ["amended-oracle-awaiting-retry"] = "s2",
        };

        foreach (var (name, target) in graded)
        {
            var scenario = SupervisorDecisionGoldenScenarios.All.Single(s => s.Name == name);
            var prompt = LlmSupervisorDecider.BuildUserPromptForTest(scenario.Context);

            prompt.ShouldContain($"[{target}]", Case.Sensitive,
                $"'{name}' is graded on targeting '{target}', so the plan-state recitation must name it — otherwise the gate measures inference off a raw payload dump, not reading");
            prompt.ShouldContain("Unfinished:", Case.Sensitive, $"'{name}' has unfinished work and the recitation must say so plainly");
        }
    }

    [Fact]
    public void A_scenario_with_an_authorized_wave_recites_the_stopped_now_verdict()
    {
        // Production composes this block on every turn once a wave has staked an obligation, so a corpus without it
        // was asking the model to choose a stop while withholding the reducer's verdict on what a stop would read as
        // — the exact perception gap behind stopping as-if-done. It was absent from all 23 prompts.
        foreach (var scenario in SupervisorDecisionGoldenScenarios.All)
        {
            var prompt = LlmSupervisorDecider.BuildUserPromptForTest(scenario.Context);
            var staked = SupervisorTapeCompletion.ProjectIfStoppedNow(scenario.Context.PriorDecisions) is not null;

            if (staked)
                prompt.ShouldContain(SupervisorStopNowRecital.Header, Case.Sensitive, $"'{scenario.Name}' has staked obligations, so production would recite the stopped-now verdict here");
            else
                prompt.ShouldNotContain(SupervisorStopNowRecital.Header, Case.Sensitive, $"'{scenario.Name}' has staked nothing yet — production omits the block, and over-rendering it would invent a contract the run does not have");
        }
    }

    [Fact]
    public void The_recital_appears_exactly_when_a_wave_has_been_authorized()
    {
        // The gate itself, stated as data rather than derived — so a fixture that stops staking (or starts staking
        // early) is caught here instead of silently changing what every scenario's prompt says.
        var silent = new HashSet<string>(StringComparer.Ordinal) { "first-turn", "planned-not-spawned", "confirmation-approved", "confirmation-feedback" };

        foreach (var scenario in SupervisorDecisionGoldenScenarios.All)
        {
            var recited = SupervisorTapeCompletion.ProjectIfStoppedNow(scenario.Context.PriorDecisions) is not null;

            recited.ShouldBe(!silent.Contains(scenario.Name),
                $"'{scenario.Name}': a tape recites the stopped-now verdict once — and only once — some spawn has staked an obligation against a planned unit");
        }
    }

    [Fact]
    public void The_stopped_now_recital_steers_in_the_direction_each_tape_actually_points()
    {
        // The live gate's answer to the first wiring of this block was unambiguous: every dimension read Unknown on
        // every tape, so the recital said "settle what is owed" against a state no action could discharge, and all
        // five arcs collapsed into plan→spawn loops. The block is only safe to show a model if a FINISHED tape's
        // recital actually reads settled — so the direction, per scenario shape, is pinned here.
        foreach (var scenario in SupervisorDecisionGoldenScenarios.All)
        {
            var prompt = LlmSupervisorDecider.BuildUserPromptForTest(scenario.Context);

            if (scenario.Name is "all-succeeded" or "three-subtask-all-succeeded" or "four-subtask-all-succeeded" or "clean-integration")
                prompt.ShouldContain("every contract dimension reads SETTLED", Case.Sensitive,
                    $"'{scenario.Name}' is a finished, fully-attested tape — an owed-forever recital here tells the model to keep working on a contract that is already met, which is the exact live regression this pins against");

            if (scenario.Name is "mixed-results" or "all-failed" or "retried-still-failed")
                prompt.ShouldContain("UNRESOLVED", Case.Sensitive,
                    $"'{scenario.Name}' has failed or unanswered obligations — a settled recital here would bless a stop-as-if-done");
        }
    }

    [Fact]
    public void The_cap_sensitivity_pair_cannot_be_passed_by_one_constant_answer()
    {
        // `unverified-resolution` and `resolve-cap-spent` carry the SAME failed reconciliation and differ only in
        // whether the resolve budget is spent. That difference is the entire measurement, and it only exists while
        // their accepted sets are DISJOINT — while both accepted Stop, a model that always stopped passed both and
        // the pair reported health having discriminated nothing.
        var withBudget = SupervisorDecisionGoldenScenarios.All.Single(s => s.Name == "unverified-resolution");
        var capSpent = SupervisorDecisionGoldenScenarios.All.Single(s => s.Name == "resolve-cap-spent");

        withBudget.AcceptedKinds.Intersect(capSpent.AcceptedKinds, StringComparer.Ordinal).ShouldBeEmpty(
            "a kind accepted by both is a constant answer that passes the pair without ever reading the cap");

        LlmSupervisorDecider.BuildUserPromptForTest(withBudget.Context)
            .ShouldNotContain("resolve cap is spent", Case.Insensitive, "the budget-remaining half must not be told the cap is gone");
        LlmSupervisorDecider.BuildUserPromptForTest(capSpent.Context)
            .ShouldContain("resolve cap is spent", Case.Insensitive, "the budget-spent half must be told, or it is the same scenario twice");
    }

    /// <summary>
    /// The scenarios whose tape leaves a Required upstream stage unevidenced, and therefore the ONLY scenarios whose
    /// prompt moved when the corpus started rendering through the full tape mirror. Pinned as data so the re-pin
    /// below has a named receipt a reviewer can check against the diff, rather than a digest nobody can attribute.
    /// </summary>
    private static readonly HashSet<string> MissingARequiredStage = new(StringComparer.Ordinal)
    {
        "agent-reported-conflict-no-integration", "all-failed", "all-succeeded", "amended-oracle-awaiting-retry",
        "amended-oracle-discarded-by-replan", "first-infra-failure", "five-subtask-middle-failed", "four-subtask-all-succeeded",
        "four-subtask-two-failed", "merge-conflict", "mixed-results",
        "multi-file-conflict", "re-plan-left-the-verdict-unchanged", "resolve-cap-spent",
        "retried-failure-succeeded", "retried-still-failed",
        "subset-conflict-across-three", "three-subtask-all-succeeded", "three-subtask-partial-failure",
        "unverified-resolution",
    };

    /// <summary>
    /// The re-pin's receipt, and the reason the corpus's numbers stay comparable across it. The corpus used to
    /// render the stopped-now block from the ASSESSMENT ALONE while the trajectory harness rendered it from the
    /// assessment PLUS the tape's upstream stage trace, so a conflicted-then-unverified fixture read LESS unresolved
    /// here than the same tape reads in production — and the pinned digest could not detect a regression in a line
    /// no scenario was able to reach.
    ///
    /// <para>This pins the delta EXACTLY: every scenario's prompt is rendered both ways, and the new one must equal
    /// the old one with the renderer's own stage line removed — so a scenario with nothing missing is byte-identical,
    /// and a scenario that is missing a stage differs by that single line and nothing else. The line is taken FROM
    /// the renderer rather than restated (this file pins arms and presence, never copy — the wording is the decider's
    /// own unit tests' job), which is also what makes the assertion survive a future rewording.</para>
    ///
    /// <para>The "before" side is ANCHORED to <see cref="DimensionsOnlyCorpusDigest"/> — the digest this corpus was
    /// actually pinned at before the mirror carried the trace. Without that anchor the receipt is tautological:
    /// both sides are re-derived from today's code, so a corpus that drifted for some unrelated reason would still
    /// differ from its own re-derivation by exactly one line and report a clean, attributable re-pin.</para>
    /// </summary>
    [Fact]
    public void Only_a_scenario_missing_a_required_stage_renders_a_different_prompt_than_before()
    {
        var profile = new ModeProfileRegistry().Resolve(RunModeKeys.Supervisor)!;
        var moved = new List<string>();

        Digest(RenderedCorpus(s => AsRenderedBeforeTheTurnRoster(DimensionsOnlyPrompt(s), s.Context), PredatesTheSupersededPins)).ShouldBe(DimensionsOnlyCorpusDigest,
            "the 'before' half of this receipt must be the corpus that was really pinned before the mirror carried the trace — if it is not, the per-scenario deltas below are a re-derivation comparing today's code with itself, and they would look clean across a drift that has nothing to do with the stage line");

        foreach (var scenario in SupervisorDecisionGoldenScenarios.All)
        {
            var projected = SupervisorTapeCompletion.ProjectIfStoppedNow(scenario.Context.PriorDecisions);

            // The rendering this corpus shipped BEFORE the mirror carried the trace: dimensions only, no profile.
            var dimensionsOnly = SupervisorStopNowRecital.Render(projected?.Assessment);
            var withTrace = scenario.Context.CompletionRecital;

            var before = DimensionsOnlyPrompt(scenario);
            var after = LlmSupervisorDecider.BuildUserPromptForTest(scenario.Context);

            // Render appends the stage line to the dimensions-only block, so the suffix past that block's length IS
            // the added line — derived, never retyped, so a reworded steer does not make this a two-file chore.
            var stageLine = withTrace is null ? string.Empty : withTrace[dimensionsOnly!.Length..];
            var missing = projected is null ? [] : UpstreamStageTrace.MissingRequired(profile, projected.ExercisedUpstreamStages);

            (stageLine.Length > 0).ShouldBe(missing.Count > 0,
                $"'{scenario.Name}': the stage line must render exactly when the supervisor profile declares a stage this tape cannot evidence — a corpus that renders it nowhere is the dimensions-only corpus under a new digest");

            (stageLine.Length == 0 ? after : after.Replace(stageLine, string.Empty, StringComparison.Ordinal)).ShouldBe(before,
                $"'{scenario.Name}': the ONLY byte that may move in this re-pin is the missing-stage line. Anything else means an unrelated block drifted into the same commit, and the corpus's scores stop being comparable across it");

            if (stageLine.Length > 0) moved.Add(scenario.Name);
        }

        moved.ShouldBe(MissingARequiredStage.ToList(), ignoreOrder: true,
            "the set of scenarios whose prompt moved must match the named receipt above — an unlisted mover is a re-pin nobody attributed");
    }

    /// <summary>
    /// The corpus's rendered-prompt digest. Every real-model score this repository reports is a measurement of THESE
    /// bytes, so a block edit anywhere in the decider silently changes what the gate measured — the assertions above
    /// pin arms and presence, which a reworded (or newly added, or quietly dropped) block slips straight past.
    ///
    /// <para>TO RE-PIN DELIBERATELY: run this test, copy the SHA-256 the failure prints into this constant, and say
    /// in the commit body WHICH block changed and why the corpus's numbers are still comparable across the change.
    /// A re-pin with no such sentence is the failure mode this exists to make visible, not a chore to be rubber-stamped.</para>
    ///
    /// <para>The superseded pin stays beside it as HISTORY, and is still asserted (over the rendering that produced
    /// it) by the re-pin receipt above — a digest whose predecessor is deleted can only ever be compared with itself.</para>
    /// </summary>
    /// <remarks>
    /// LAST RE-PIN: the prompt gained the bounded per-unit MODEL/TOKEN/COST recitation produced by
    /// <see cref="SupervisorBudgetRecitation.RenderUnits"/>. This is an intentional rendering change for tapes with
    /// durable agent attempts: the supervisor can now compare retries and model spend by planned unit instead of
    /// reasoning from a run-wide total. The superseded corpus still reproduces exactly after
    /// <see cref="AsRenderedBeforeTheTurnRoster"/> removes this one insertion together with the earlier named blocks.
    /// Unit tests pin aggregation, unknown-price honesty, and the long-run output bound.
    ///
    /// PREVIOUS RE-PIN: corpus GROWTH and nothing else — <c>first-infra-failure</c> joined it (arc-3 item 4.3's
    /// residual), a 27th decision point for the FIRST time a subtask's check comes back UNRUNNABLE, before any
    /// re-plan or co-sign is on the tape. No rendering changed: <see cref="LlmSupervisorDecider.InfraSteerFor"/>'s
    /// <c>None</c>/<c>None</c> arm renders <see cref="LlmSupervisorDecider.ReplanThisItemWithASatisfiableCheck"/>,
    /// pinned at the unit level as deliberately unmoving copy (<c>SupervisorDeciderTests</c>: "the FIRST time a
    /// check comes back unrunnable, authoring a satisfiable one is honest advice — and its wording must not
    /// move") — what this scenario adds is the FIRST exercise of that exact arm through the corpus the real-model
    /// gate actually scores; no prior scenario reached it (every earlier "Failed" tape is WORK-classed, not infra).
    ///
    /// <para>The corpus's numbers stay comparable because NO pre-existing scenario's prompt moved, and that is
    /// DERIVED rather than claimed: <see cref="The_rendered_corpus_matches_its_pinned_digest"/> recomputes today's
    /// rendering over the 25 scenarios that predate the roster pin and requires <see cref="StaticVerbRosterCorpusDigest"/>
    /// back through its wind-back, and over the 23 older ones for the two pins beneath it — both exclusion sets
    /// (<see cref="AddedSinceTheRosterPin"/>, <see cref="AddedSinceTheSupersededPins"/>) now exclude this scenario
    /// too, the same way they already excluded the prior growth. The new scenario's own roster OFFERS
    /// <c>amend_acceptance</c> (<see cref="SupervisorAmendPrecondition.AnyAmendableUnit"/> never required a prior
    /// re-plan, only a graded infra-classed failure), so <see cref="AmendOfferedScenarios"/> grows to three even
    /// though its accepted kinds do not include the verb; its tape leaves the same Required stage unevidenced as
    /// its co-sign-loop siblings, so <see cref="MissingARequiredStage"/> grows too.</para>
    ///
    /// <para>PREVIOUS RE-PIN: corpus GROWTH and nothing else — <c>re-plan-left-the-verdict-unchanged</c> joined it,
    /// a 26th decision point for the re-plan fixed point with no co-sign in it (a re-plan spent on an unrunnable
    /// check whose verdict did not move: the shape arm
    /// <c>The_real_model_observes_a_real_conflict_and_chooses_to_resolve</c> failed ~25-40% of its attempts on,
    /// <c>plan→spawn→plan×6→stop</c>, runs 34104701023 and 34101026801 attempt 2). Its tape is
    /// <c>plan→spawn→re-plan→re-spawn</c>: the RE-SPAWN is what makes the accepted pair the only defensible answer,
    /// because a re-plan nobody has run yet is a plan whose honest next move is to STAGE it, and a golden must leave
    /// one right move (<see cref="SupervisorReplanStanding"/> splits those two tapes; the unrun one is pinned at the
    /// unit level, where the ambiguity does not exist). The new steer is derived from a re-plan whose re-graded
    /// verdict came back identical (<see cref="SupervisorReplanStanding"/>), and the corpus's only other tape with a
    /// re-plan on it carries co-signs, so its units read <see cref="SupervisorAmendStanding.Discarded"/> and keep
    /// the steer they already had.</para>
    ///
    /// <para>EARLIER RE-PIN: the amend gate's evidence read widened past <see cref="SupervisorPlanWindow"/> to the
    /// whole tape (<c>SupervisorAmendPrecondition</c>), so <c>amend_acceptance</c> moved from the roster's WITHHELD
    /// half to its OFFERED half on <c>amended-oracle-discarded-by-replan</c> — the one tape in the corpus whose
    /// re-plan closed the window over the infra grade an amendment answers. That scenario grades
    /// <c>amend_acceptance</c> and its Discarded steer names it, while the prompt it had been measured under
    /// reported the verb unavailable one screen below, and the decision eval's model answered with a third one
    /// (<c>merge</c>, run 34085079257 at 24/25). <see cref="Exactly_the_amendable_tapes_offer_the_amend_verb"/>
    /// pins which rosters offer it — the set was EMPTY across all 25 before that change.</para>
    /// </remarks>
    private const string GoldenPromptDigest = "99ee159b12d165b89fa9dbbe4adf9ce6aef9935aad69b7c1972244290239187f";

    /// <summary>
    /// The pin this corpus carried while the VERB ROSTER was a static sentence in the turn-invariant system prompt —
    /// seven verbs and their meanings, listed identically on every turn, while the action mask in the user prompt
    /// named the one the server would refuse. Superseded because that is one prompt with two rosters: golden
    /// <c>resolve-cap-spent</c> passed four main runs in a row on the gating Anthropic wire and then failed 2 of 4
    /// branch lanes, once answering <c>resolve</c> — the verb the mask three lines below withheld — and once
    /// <c>merge</c>, the merge already recorded as conflicted. The roster is now rendered per turn FROM the mask
    /// (<see cref="SupervisorActionRoster"/>), so a withheld verb is never on the menu it is withheld from.
    ///
    /// <para>The corpus's numbers stay comparable across the re-pin, and that is DERIVED rather than claimed: every
    /// scenario's prompt is wound back through <see cref="AsRenderedBeforeTheTurnRoster"/> — the roster replaced by
    /// the mask block it grew out of (as that mask read before it could withhold <c>amend_acceptance</c>), the
    /// conflicted-integration block's cap-aware closing line replaced by the invitation it retired, and the prompt's
    /// cap-aware closing sentence replaced by the unconditional one — and this pin then reproduces itself over the
    /// 25 scenarios it was taken at (<see cref="AddedSinceTheRosterPin"/> excludes the later ones), while the three
    /// 23-scenario anchors below reproduce theirs over the subset they
    /// were each taken at. So the moved bytes are exactly those three blocks and nothing else: the roster on every
    /// scenario, and the closing line and closing sentence on the tapes that record a conflict with the resolve cap
    /// spent (<c>resolve-cap-spent</c>, <c>verified-resolution</c>). No scenario's <c>AcceptedKinds</c> changed, and
    /// none acquired a menu entry for a verb its own tape cannot reach — which
    /// <see cref="No_scenario_steers_toward_a_verb_its_tape_cannot_reach"/>,
    /// <see cref="Every_scenario_renders_the_action_mask_arm_its_tape_implies"/> and
    /// <see cref="The_cosign_pair_gets_the_amend_arm_its_own_precondition_agrees_with"/> re-derive off the
    /// mask and the amend gate themselves.</para>
    /// </summary>
    private const string StaticVerbRosterCorpusDigest = "d4c31246c3aefb766e4e913dc8fbbf9dc25f428fc5a2b0805e9087a6963ee42c";

    /// <summary>
    /// The pin this corpus carried while it held 23 scenarios — before the two co-sign scenarios joined it. They
    /// are the co-sign loop's own decision points: an infra-classed unit whose oracle a human has APPROVED a
    /// replacement for, where the only move that consumes the co-sign is a retry
    /// (<c>amended-oracle-awaiting-retry</c>) — and the same unit one re-plan later, its repair already discarded,
    /// where the only moves left are re-proposing the amendment or a human ruling
    /// (<c>amended-oracle-discarded-by-replan</c>). Real-model run 34066916864 answered the second with a plan
    /// eight times over and force-stopped with nothing integrated.
    ///
    /// <para>The corpus's numbers stay comparable across the re-pin because NOTHING that was already in it moved:
    /// the new steer is derived from a co-signed amendment on the tape, and no pre-existing scenario has one. That
    /// is asserted rather than claimed — <see cref="The_rendered_corpus_matches_its_pinned_digest"/> recomputes
    /// today's rendering over the 23 scenarios that predate this pin and requires exactly this value back. It has
    /// now survived two corpus growths and two edits to the amended readings — the second of which reached into the
    /// plan-state recitation, a block EVERY scenario renders, and this receipt is what proves it moved none of
    /// them. That is the whole point of keeping it.</para>
    /// </summary>
    private const string PreCosignScenarioCorpusDigest = "4b44d4d228bd23b4dfaad94cc0f403e641af82fc221db31f8b0d35772b4d4bea";

    /// <summary>
    /// The pin this corpus carried while the stopped-now block's steer was a CONSTANT ("Land that work, stop with
    /// outcome 'gave_up', or ask_human"), regardless of which verbs the tape still reached. Superseded because that
    /// constant is what the corpus's one failing scenario followed: <c>resolve-cap-spent</c> answered <c>merge</c>
    /// (run 34027621996, golden 22/23) because with a conflicted integration recorded and the resolve cap spent,
    /// <c>merge</c> was the only landing reading of "Land that work" — and it re-attempts the same conflicted merge.
    ///
    /// <para>The corpus's numbers stay comparable across the re-pin because the moved bytes are confined to ONE
    /// clause of ONE line, in exactly the five scenarios that both record a conflict
    /// (<see cref="ConflictedScenarios"/>) and render a stage line (<see cref="MissingARequiredStage"/>) — and every
    /// one of them moves TOWARD its own already-pinned expectation rather than away from it. The four with resolve
    /// runway left now name <c>resolve</c>, whose accepted set is exactly {resolve}; <c>resolve-cap-spent</c>, whose
    /// accepted set is {stop, ask_human}, names only those two. No scenario's <c>AcceptedKinds</c> changed, and no
    /// scenario acquired a steer toward a verb the action mask forbids on its own tape
    /// (<see cref="No_scenario_steers_toward_a_verb_its_tape_cannot_reach"/>). Every other scenario is
    /// byte-identical, which <see cref="Only_a_scenario_missing_a_required_stage_renders_a_different_prompt_than_before"/>
    /// re-derives against the anchor below rather than taking on trust.</para>
    ///
    /// <para>It shipped ASSERTED BY NOTHING (#1795 added the constant and the sentence claiming a receipt
    /// re-derives it, but no test ever recomputed it) — which is precisely the state this file's own rule calls
    /// out: a digest no test recomputes can only ever be compared with itself. The receipt in
    /// <see cref="The_rendered_corpus_matches_its_pinned_digest"/> now recomputes it, over the same 23-scenario
    /// corpus it was taken at (it WAS this corpus's <c>GoldenPromptDigest</c> at 5618fc262^, where the count
    /// assertion read 23), through this commit's wind-back plus
    /// <see cref="AsSteeredBeforeTheReachAwareSteer"/>.</para>
    /// </summary>
    private const string ConstantSteerCorpusDigest = "9a06aec3056ee4851e8ccd69cdb67585b6b3f20a4414ec03be2dc0ea188426ba";

    /// <summary>The pin this corpus carried while the stopped-now block was rendered from the ASSESSMENT ALONE — no stage trace, no profile, no mode. Superseded, never deleted: it is the fixed point the re-pin receipt measures the current rendering against.</summary>
    private const string DimensionsOnlyCorpusDigest = "40e8c14c75e6f90d017a4780aa9479fc782379f7851f950a04a1962c9ddee4f8";

    [Fact]
    public void The_rendered_corpus_matches_its_pinned_digest()
    {
        // The historical receipt: wind the four named blocks back to what they replaced — the roster to the
        // mask block it grew out of, the cap-aware closing line to the invitation it retired, the cap-aware ending
        // to its unconditional predecessor, and the newly inserted per-unit cost recital to absence — and the pin that
        // stood before them must return, over the 25 scenarios that pin was measured at (AddedSinceTheRosterPin —
        // the per-pin-corpus rule the set below states). A block that drifted into this commit fails here, where the named
        // ones are still separable from it, instead of hiding inside the re-pin below.
        Digest(RenderedCorpus(s => AsRenderedBeforeTheTurnRoster(LlmSupervisorDecider.BuildUserPromptForTest(s.Context), s.Context), PredatesTheRosterPin)).ShouldBe(StaticVerbRosterCorpusDigest,
            "undoing the four named prompt blocks no longer reproduces the pin this corpus carried before them — an unrelated delta entered the historical reconstruction");

        // This re-pin's receipt: over the scenarios that predate it, today's rendering still digests to the
        // superseded pin — so the move is corpus GROWTH and nothing else, and every score taken under the old pin
        // remains comparable with one taken under the new one.
        Digest(RenderedCorpus(s => AsRenderedBeforeTheTurnRoster(LlmSupervisorDecider.BuildUserPromptForTest(s.Context), s.Context), PredatesTheSupersededPins)).ShouldBe(PreCosignScenarioCorpusDigest,
            "a pre-existing scenario's prompt moved in the same commit that grew the corpus — the growth is then not the whole story, and the digest below cannot be attributed to it");

        // One commit further back, and the receipt #1795 said existed but never wrote: undo the reach-aware steer
        // on top of this commit's wind-back and the constant-steer pin must return over the same 23 scenarios it
        // was taken at. Without this the constant is a digest nobody recomputes — comparable only with itself.
        Digest(RenderedCorpus(s => AsSteeredBeforeTheReachAwareSteer(AsRenderedBeforeTheTurnRoster(LlmSupervisorDecider.BuildUserPromptForTest(s.Context), s.Context), s.Context), PredatesTheSupersededPins)).ShouldBe(ConstantSteerCorpusDigest,
            "undoing the reach-aware steer no longer reproduces the pin this corpus carried before it — so the steer is not the whole delta of that change either, and the chain of superseded pins has a gap in it");

        var digest = Digest(RenderedCorpus());

        digest.ShouldBe(GoldenPromptDigest,
            $"the rendered golden prompts changed. If that was intended, re-pin GoldenPromptDigest to '{digest}' and name the block that changed in the commit body; if it was not, a decider edit has silently moved what every real-model score measures.");
    }

    /// <summary>
    /// The scenarios whose tape records a conflicted integration — the only ones whose steer this re-pin could move.
    /// Pinned as data so the digest above has a named receipt, exactly like <see cref="MissingARequiredStage"/>.
    ///
    /// <para>Six tapes, but only FIVE prompts moved: <c>verified-resolution</c> records a conflict AND has its cap
    /// spent, yet its reconciliation VERIFIED, so its tape evidences Integrate, no stage line renders at all, and
    /// its prompt is byte-identical. The movers are precisely this set ∩ <see cref="MissingARequiredStage"/> — which
    /// is why both receipts are kept rather than merged into one.</para>
    /// </summary>
    private static readonly HashSet<string> ConflictedScenarios = new(StringComparer.Ordinal)
    {
        "merge-conflict", "multi-file-conflict", "resolve-cap-spent", "subset-conflict-across-three",
        "unverified-resolution", "verified-resolution",
    };

    /// <summary>
    /// The scenarios whose roster OFFERS <c>amend_acceptance</c> — the named receipt for the current
    /// <see cref="GoldenPromptDigest"/>, exactly like <see cref="ConflictedScenarios"/> and
    /// <see cref="MissingARequiredStage"/> are for theirs. It is a ONE-element set because a tape is only amendable
    /// where a graded infra failure stands with no co-sign in force, and this corpus records that in one place.
    /// </summary>
    private static readonly HashSet<string> AmendOfferedScenarios = new(StringComparer.Ordinal)
    {
        "amended-oracle-discarded-by-replan", "re-plan-left-the-verdict-unchanged", "first-infra-failure",
    };

    /// <summary>
    /// WHICH scenarios' roster block offers the verb. Before the amend gate's evidence read widened to the whole
    /// tape this set was EMPTY across all 25 scenarios — the mask withheld the verb everywhere, the
    /// discarded-co-sign tape included — so pinning the set is what turns "these tapes and no others" from a
    /// sentence in a doc-comment into a fact a build can refute. The sibling anchors prove nothing outside the roster
    /// block moved; this proves whose roster block did.
    ///
    /// <para>It is a TWO-element set now: <c>re-plan-left-the-verdict-unchanged</c> is the same unrunnable-check
    /// evidence with no co-sign on it, so the gate admits an amendment there too — and its steer names the verb,
    /// which is only honest while the menu offers it.</para>
    ///
    /// <para>THREE now: <c>first-infra-failure</c> (arc-3 item 4.3) is the SAME graded-infra-with-no-co-sign evidence
    /// one turn earlier — before any re-plan has been authored over the verdict at all. The gate's admission test
    /// (<see cref="SupervisorAmendPrecondition.AnyAmendableUnit"/>) never required a prior re-plan, only a graded
    /// infra-classed failure, so the menu already offered the verb here; nothing in this fact changed, only the
    /// corpus's coverage of it. Its steer does NOT name the verb (<see cref="LlmSupervisorDecider.ReplanThisItemWithASatisfiableCheck"/>
    /// pins that first-time copy as deliberately unmoving), so this is the one member of the set whose accepted
    /// kinds do not include <c>amend_acceptance</c> — offered on the menu without being the preferred move, which is
    /// the ordinary relationship every never-masked verb already has to every steer that does not name it.</para>
    /// </summary>
    [Fact]
    public void Exactly_the_amendable_tapes_offer_the_amend_verb()
    {
        SupervisorDecisionGoldenScenarios.All
            .Where(s => OfferedInPrompt(LlmSupervisorDecider.BuildUserPromptForTest(s.Context)).Contains(SupervisorDecisionKinds.AmendAcceptance))
            .Select(s => s.Name)
            .ShouldBe(AmendOfferedScenarios.ToList(), ignoreOrder: true,
                "the set of scenarios whose menu offers 'amend_acceptance' must match the named receipt beside the digest — an unlisted one is a re-pin nobody attributed, and a missing one means the gate silently re-withheld the verb its own steer names");
    }

    /// <summary>
    /// THE regression this corpus exists to catch from now on: no scenario's prompt may steer the brain toward a verb
    /// its own tape cannot reach. <c>resolve-cap-spent</c> is the live miss (run 34027621996) — a conflicted
    /// integration recorded, the resolve cap spent, and the steer still reading "Land that work", whose only landing
    /// verb is <c>merge</c>: a blind repeat of the conflicted merge already on the tape. The model chose exactly that.
    ///
    /// <para>Derived from <see cref="SupervisorActionMask"/> rather than restated, so the assertion is the SAME
    /// reading the mask three lines below the steer publishes — a copy here could drift into blessing precisely the
    /// contradiction this shares a reader to prevent.</para>
    /// </summary>
    [Fact]
    public void No_scenario_steers_toward_a_verb_its_tape_cannot_reach()
    {
        var conflicted = new List<string>();

        foreach (var scenario in SupervisorDecisionGoldenScenarios.All)
        {
            var prompt = LlmSupervisorDecider.BuildUserPromptForTest(scenario.Context);
            var reach = SupervisorActionMask.LandingReachFor(scenario.Context.PriorDecisions, scenario.Context.MaxResolveAttempts);

            if (reach != SupervisorLandingReach.Unconstrained) conflicted.Add(scenario.Name);

            if (scenario.Context.CompletionRecital is null) continue;

            var steer = SupervisorStopNowRecital.SteerFor(reach);

            // A rendered stage line carries this reach's steer and no other — the two other steers must be absent.
            if (scenario.Context.CompletionRecital.Contains(SupervisorStopNowRecital.RefusalLead, StringComparison.Ordinal)
                || scenario.Context.CompletionRecital.Contains(SupervisorStopNowRecital.AdvisoryLead, StringComparison.Ordinal))
            {
                prompt.ShouldContain(steer, Case.Sensitive, $"'{scenario.Name}': the stage line must carry the steer its own tape earns");

                if (reach == SupervisorLandingReach.NoLandingReachable)
                    scenario.Context.CompletionRecital.ShouldNotContain("Land that work", Case.Insensitive,
                        $"'{scenario.Name}': a conflict is recorded and a further resolve would FORCE-STOP the run, so 'merge' is the only landing reading left — and it re-attempts the conflict already on the tape");

                if (reach != SupervisorLandingReach.ReconcileFirst)
                    scenario.Context.CompletionRecital.ShouldNotContain("Resolve the recorded conflict", Case.Sensitive,
                        $"'{scenario.Name}': the mask forbids resolve on this tape, so the steer must not offer it");
            }
        }

        conflicted.ShouldBe(ConflictedScenarios.ToList(), ignoreOrder: true,
            "the set of scenarios whose tape records a conflict must match the named receipt beside the digest");
    }

    [Fact]
    public void The_digest_covers_every_scenario_in_the_corpus()
    {
        // A digest over a shrinking corpus is a green light for a shrinking corpus. Pin the count beside the bytes.
        SupervisorDecisionGoldenScenarios.All.Count.ShouldBe(27, "a scenario was added or dropped — re-pin this count together with the digest");
        SupervisorDecisionGoldenScenarios.All.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count()
            .ShouldBe(SupervisorDecisionGoldenScenarios.All.Count, "two scenarios share a name — the digest's ordering would not be stable");
    }

    /// <summary>
    /// Every scenario carries the fixture ids the corpus THINKS it carries. <c>All</c> is a static initializer, and
    /// a <c>static readonly</c> field declared below it in the same class is still its default value while every
    /// scenario is being built — so the brain-model id and the authorized plan ref, both declared below <c>All</c>,
    /// read back as <c>Guid.Empty</c> in all 23 contexts, while the E2E and the drift tests that name the same
    /// symbols directly got the real values. Nothing failed: an all-zeros plan ref still PARSES, so obligations were
    /// staked against it and every downstream block rendered plausibly.
    ///
    /// <para>Pinned on the VALUES rather than on the declaration mechanism, so it keeps holding however the ids are
    /// later expressed — and fails the moment one of them silently becomes a default again.</para>
    /// </summary>
    [Fact]
    public void Every_scenario_carries_the_fixture_ids_the_corpus_declares()
    {
        SupervisorDecisionGoldenScenarios.BrainModelRowId.ShouldNotBe(Guid.Empty, "the brain-model row id is the fixture's identity — an empty one is a default, not a pick");

        foreach (var scenario in SupervisorDecisionGoldenScenarios.All)
        {
            scenario.Context.SupervisorModelId.ShouldBe(SupervisorDecisionGoldenScenarios.BrainModelRowId,
                $"'{scenario.Name}' was built with a different brain id than the corpus declares — the real-model lane resolves the declared one, so the two lanes would be running different fixtures");

            foreach (var plan in scenario.Context.PriorDecisions.Where(d => d.DecisionKind == SupervisorDecisionKinds.Plan))
            {
                var planRef = SupervisorOutcome.ReadPlanRef(plan.OutcomeJson);

                planRef.ShouldNotBeNull($"'{scenario.Name}' has a plan whose outcome carries no readable ref — production stakes NOTHING without one, so the whole stopped-now verdict would vanish");
                planRef!.Value.WorkPlanId.ShouldNotBe(Guid.Empty, $"'{scenario.Name}' stakes its obligations against an all-zeros plan ref — it parses, so nothing complains, and the fixture silently stops describing the run it claims to");
            }
        }
    }

    /// <summary>Every scenario's rendered prompt, name-ordered and name-labelled — deterministic over the corpus, so the digest moves only when the RENDERING moves.</summary>
    private static string RenderedCorpus() => RenderedCorpus(s => LlmSupervisorDecider.BuildUserPromptForTest(s.Context));

    /// <summary>The same canonical corpus over an ALTERNATIVE rendering, and optionally over a SUBSET, so a historical digest is recomputed by exactly the concatenation that produced it — over exactly the scenarios that existed when it was taken. A second hand-rolled loop would be its own drift risk.</summary>
    private static string RenderedCorpus(Func<SupervisorGoldenScenario, string> render, Func<SupervisorGoldenScenario, bool>? include = null)
    {
        var builder = new StringBuilder();

        foreach (var scenario in SupervisorDecisionGoldenScenarios.All.Where(s => include?.Invoke(s) != false).OrderBy(s => s.Name, StringComparer.Ordinal))
            builder.Append("\u0000").Append(scenario.Name).Append("\u0000").Append(render(scenario));

        return builder.ToString();
    }

    /// <summary>
    /// Scenarios added AFTER the superseded pins below were taken. They are excluded from those pins' recomputation
    /// rather than folded into a re-pin of them: a superseded digest re-pinned over today's corpus is a
    /// re-derivation of today's code, and every receipt anchored to it silently degrades from "the before half is
    /// the rendering that really shipped" to "the before half is whatever this build produces".
    ///
    /// <para>ONE set serves ALL THREE 23-scenario pins, and that is only sound while they were taken over the SAME
    /// corpus — they were: <see cref="DimensionsOnlyCorpusDigest"/>, <see cref="PreCosignScenarioCorpusDigest"/> and
    /// <see cref="ConstantSteerCorpusDigest"/> all stood at the corpus's 23 pre-co-sign scenarios. The fourth,
    /// <see cref="StaticVerbRosterCorpusDigest"/>, was taken at 25, so it gets its OWN set
    /// (<see cref="AddedSinceTheRosterPin"/>) — the split this comment demanded the moment a pin was taken at a
    /// different corpus size, because excluding from a pin a scenario it already covered would silently recompute
    /// that pin over a corpus it never measured, which is exactly the degradation above.</para>
    /// </summary>
    private static readonly HashSet<string> AddedSinceTheSupersededPins = new(StringComparer.Ordinal)
    {
        "amended-oracle-awaiting-retry", "amended-oracle-discarded-by-replan", "re-plan-left-the-verdict-unchanged", "first-infra-failure",
    };

    /// <summary>Scenarios added after <see cref="StaticVerbRosterCorpusDigest"/> was taken. That pin measured 25 scenarios — the two co-sign ones INCLUDED, which is why they are absent here and why this cannot be folded into the 23-scenario set above.</summary>
    private static readonly HashSet<string> AddedSinceTheRosterPin = new(StringComparer.Ordinal)
    {
        "re-plan-left-the-verdict-unchanged", "first-infra-failure",
    };

    private static bool PredatesTheSupersededPins(SupervisorGoldenScenario scenario) => !AddedSinceTheSupersededPins.Contains(scenario.Name);

    private static bool PredatesTheRosterPin(SupervisorGoldenScenario scenario) => !AddedSinceTheRosterPin.Contains(scenario.Name);

    /// <summary>
    /// The conflicted-integration block's closing line as it read BEFORE it became cap-aware — the copy that
    /// offered <c>resolve</c> and a re-<c>merge</c> on a tape where the mask had withdrawn the first and the
    /// stopped-now steer the second. Restated rather than derived because the renderer no longer holds this exact
    /// string: the with-budget arm still renders the invitation, but with its <c>afterResolve</c> clause filled per
    /// call site, so there is no constant to point at. It is frozen HISTORY — the bytes a superseded digest was
    /// taken over — and a reword of the live line must not silently move it. Only the merge arm is needed: every
    /// cap-spent conflict in this corpus is a conflicted MERGE, and a tape that ever reached the blocked-spawn arm
    /// with a spent cap would fail the anchors below loudly rather than silently.
    /// </summary>
    private const string ResolveInvitedOnAConflictedIntegration =
        "    To reconcile: choose 'resolve' — the server spawns ONE agent that reconciles these branches, builds, and runs the tests, then you merge again. Or stop to leave the conflict for a human.";

    /// <summary>
    /// One scenario's prompt wound back to the rendering that produced the superseded pins below. FOUR blocks are
    /// undone, and they are the four later changes moved:
    /// <list type="number">
    ///   <item>the turn's VERB ROSTER, replaced by the action mask it grew out of — as that mask read before it
    ///         could withhold <c>amend_acceptance</c> (<see cref="AsMaskedBeforeTheAmendArm"/>);</item>
    ///   <item>the conflicted-integration block's cap-aware closing line, replaced by the invitation it retired;</item>
    ///   <item>the prompt's cap-aware CLOSING SENTENCE, replaced by the unconditional "then merge the successful
    ///         results, then stop." it retired.</item>
    ///   <item>the per-unit MODEL/TOKEN/COST recitation, removed because it did not exist when any superseded digest
    ///         was recorded.</item>
    /// </list>
    /// It exists because the roster renders on EVERY turn where the mask rendered on most, so a superseded digest
    /// recomputed over today's raw rendering can no longer reproduce itself, and every receipt below would have to
    /// be deleted or re-pinned into a tautology.
    ///
    /// <para>Winding them back keeps the receipts, and makes them stronger than a re-pin would: the roster is a pure
    /// INSERTION over the mask, the unit-cost recital is a pure insertion, and the other two are pure substitutions,
    /// so undoing exactly those four must return
    /// the pre-commit bytes — which is what the anchors assert by still reproducing their old digests over the
    /// corpus each was taken at. Anything else that drifted into this commit shows up as a failure here rather than
    /// as a digest nobody can attribute.</para>
    ///
    /// <para>THE COST, stated plainly: every anchor that routes through this helper is now BLIND to the four blocks
    /// it undoes. A reword of the roster, the per-unit cost recital, the cap-aware closing line, or the closing
    /// sentence moves no superseded digest — by construction, since the wind-back derives the roster and cost-recital
    /// bytes from today's renderers. Only the live <see cref="GoldenPromptDigest"/> catches a change in them, so a
    /// re-pin of THAT constant is the only
    /// place such a change becomes visible, and the per-block unit tests
    /// (<c>SupervisorActionRosterTests</c>) are what pin the copy itself.</para>
    /// </summary>
    private static string AsRenderedBeforeTheTurnRoster(string prompt, SupervisorTurnContext context)
    {
        var roster = $"{Environment.NewLine}{SupervisorActionRoster.Render(context)}{Environment.NewLine}";
        var mask = AsMaskedBeforeTheAmendArm(context) is { } withheld ? $"{Environment.NewLine}{withheld}{Environment.NewLine}" : string.Empty;
        var unitCosts = SupervisorBudgetRecitation.RenderUnits(context.PriorDecisions, context.ModelPrices);
        var beforeUnitCosts = unitCosts is null ? prompt : prompt.Replace($"{Environment.NewLine}{unitCosts}{Environment.NewLine}", string.Empty, StringComparison.Ordinal);

        return beforeUnitCosts
            .Replace(roster, mask, StringComparison.Ordinal)
            .Replace(LlmSupervisorDecider.ResolveWithdrawnOnAConflictedIntegration, ResolveInvitedOnAConflictedIntegration, StringComparison.Ordinal)
            .Replace(LlmSupervisorDecider.ClosingCannotLand, LlmSupervisorDecider.ClosingLandsWithAMerge, StringComparison.Ordinal);
    }

    /// <summary>The action mask as it rendered before <c>amend_acceptance</c> joined the verbs it can withhold: resolve's line alone, or NOTHING when resolve was the available one. Derived by deleting the amend line from today's render rather than restating the old format, so a reworded resolve reason stays a one-file change.</summary>
    private static string? AsMaskedBeforeTheAmendArm(SupervisorTurnContext context)
    {
        if (SupervisorActionMask.Render(context) is not { } mask) return null;

        var kept = mask.Split('\n').Where(line => !line.StartsWith($"- {SupervisorDecisionKinds.AmendAcceptance} — ", StringComparison.Ordinal)).ToList();

        return kept.Count > 1 ? string.Join('\n', kept) : null;
    }

    /// <summary>The same prompt one commit further back: the stopped-now steer as it read while it was a CONSTANT, before <see cref="SupervisorActionMask.LandingReachFor"/> narrowed it to the verbs a tape can still reach. Taken FROM the renderer's own <c>Unconstrained</c> arm — which IS the retired constant — so this cannot drift into restating copy.</summary>
    private static string AsSteeredBeforeTheReachAwareSteer(string prompt, SupervisorTurnContext context)
    {
        var reach = SupervisorActionMask.LandingReachFor(context.PriorDecisions, context.MaxResolveAttempts);

        return reach == SupervisorLandingReach.Unconstrained
            ? prompt
            : prompt.Replace(SupervisorStopNowRecital.SteerFor(reach), SupervisorStopNowRecital.SteerFor(SupervisorLandingReach.Unconstrained), StringComparison.Ordinal);
    }

    /// <summary>One scenario's prompt as this corpus rendered it BEFORE the mirror carried the trace: the stopped-now block from the assessment ALONE — no stage trace, no profile, no enforcement mode.</summary>
    private static string DimensionsOnlyPrompt(SupervisorGoldenScenario scenario) =>
        LlmSupervisorDecider.BuildUserPromptForTest(scenario.Context with
        {
            CompletionRecital = SupervisorStopNowRecital.Render(SupervisorTapeCompletion.ProjectIfStoppedNow(scenario.Context.PriorDecisions)?.Assessment),
        });

    private static string Digest(string corpus) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(corpus))).ToLowerInvariant();

    [Fact]
    public void The_negative_controls_exclude_resolve_from_their_accepted_set()
    {
        // Cheap structural guard: a negative control that accidentally accepted Resolve would look green while
        // measuring nothing. The live gate cannot catch this — an accepted set is fixture data, not model output.
        foreach (var name in new[] { "resolve-bait-clean-integration", "agent-reported-conflict-no-integration", "resolve-cap-spent" })
        {
            var scenario = SupervisorDecisionGoldenScenarios.All.Single(s => s.Name == name);

            scenario.AcceptedKinds.ShouldNotContain(CodeSpace.Messages.Agents.SupervisorDecisionKinds.Resolve,
                $"'{name}' exists to prove the model does NOT resolve here");
        }
    }
}
