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

    [Fact]
    public void Every_scenario_renders_the_action_mask_arm_its_tape_implies()
    {
        foreach (var scenario in SupervisorDecisionGoldenScenarios.All)
        {
            var prompt = LlmSupervisorDecider.BuildUserPromptForTest(scenario.Context);
            var masked = prompt.Contains(SupervisorActionMask.Header, StringComparison.Ordinal);

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

        prompt.ShouldContain(LlmSupervisorDecider.InfraSteerFor(SupervisorAmendStanding.AwaitingRetry), Case.Sensitive,
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

        prompt.ShouldContain(LlmSupervisorDecider.InfraSteerFor(SupervisorAmendStanding.Discarded), Case.Sensitive,
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
        "amended-oracle-discarded-by-replan", "five-subtask-middle-failed", "four-subtask-all-succeeded",
        "four-subtask-two-failed", "merge-conflict", "mixed-results",
        "multi-file-conflict", "resolve-cap-spent", "retried-failure-succeeded", "retried-still-failed",
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
    private const string GoldenPromptDigest = "157c10446744d26e41c45112e46a3c7635106176d9cb366745737d5c7ea85b33";

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
    /// the mask block it grew out of, and the conflicted-integration block's cap-aware closing line replaced by the
    /// invitation it retired — and both superseded anchors then reproduce their own digests over the whole pre-pin
    /// corpus. So the moved bytes are exactly those two blocks and nothing else: the roster on every scenario, and
    /// the closing line on the two that record a conflict with the resolve cap spent (<c>resolve-cap-spent</c>,
    /// <c>verified-resolution</c>). No scenario's <c>AcceptedKinds</c> changed, and none acquired a menu entry for a
    /// verb its own tape cannot reach — which <see cref="No_scenario_steers_toward_a_verb_its_tape_cannot_reach"/>
    /// and <see cref="Every_scenario_renders_the_action_mask_arm_its_tape_implies"/> re-derive off the mask itself.</para>
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
    /// </summary>
    private const string ConstantSteerCorpusDigest = "9a06aec3056ee4851e8ccd69cdb67585b6b3f20a4414ec03be2dc0ea188426ba";

    /// <summary>The pin this corpus carried while the stopped-now block was rendered from the ASSESSMENT ALONE — no stage trace, no profile, no mode. Superseded, never deleted: it is the fixed point the re-pin receipt measures the current rendering against.</summary>
    private const string DimensionsOnlyCorpusDigest = "40e8c14c75e6f90d017a4780aa9479fc782379f7851f950a04a1962c9ddee4f8";

    [Fact]
    public void The_rendered_corpus_matches_its_pinned_digest()
    {
        // This re-pin's receipt: over the scenarios that predate it, today's rendering still digests to the
        // superseded pin — so the move is corpus GROWTH and nothing else, and every score taken under the old pin
        // remains comparable with one taken under the new one.
        Digest(RenderedCorpus(s => AsRenderedBeforeTheTurnRoster(LlmSupervisorDecider.BuildUserPromptForTest(s.Context), s.Context), PredatesTheSupersededPins)).ShouldBe(PreCosignScenarioCorpusDigest,
            "a pre-existing scenario's prompt moved in the same commit that grew the corpus — the growth is then not the whole story, and the digest below cannot be attributed to it");

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
        SupervisorDecisionGoldenScenarios.All.Count.ShouldBe(25, "a scenario was added or dropped — re-pin this count together with the digest");
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
    /// <para>ONE set serves BOTH superseded pins, and that is only sound while they were taken over the SAME
    /// corpus — they were: <see cref="DimensionsOnlyCorpusDigest"/> and <see cref="PreCosignScenarioCorpusDigest"/>
    /// both stood at the corpus's 23 pre-co-sign scenarios. The moment a pin is taken at a different corpus size,
    /// split this into a per-pin set: excluding from a pin a scenario it already covered would silently recompute
    /// that pin over a corpus it never measured, which is exactly the degradation above.</para>
    /// </summary>
    private static readonly HashSet<string> AddedSinceTheSupersededPins = new(StringComparer.Ordinal)
    {
        "amended-oracle-awaiting-retry", "amended-oracle-discarded-by-replan",
    };

    private static bool PredatesTheSupersededPins(SupervisorGoldenScenario scenario) => !AddedSinceTheSupersededPins.Contains(scenario.Name);

    /// <summary>
    /// The conflicted-integration block's closing line as it read BEFORE it became cap-aware — the copy that
    /// offered <c>resolve</c> and a re-<c>merge</c> on a tape where the mask had withdrawn the first and the
    /// stopped-now steer the second. Frozen history, not live copy: it is deleted from the renderer, so it can
    /// never be reworded again and restating it here cannot become a two-file chore. Only the merge arm is needed —
    /// every cap-spent conflict in this corpus is a conflicted MERGE, and a tape that ever reached the blocked-spawn
    /// arm with a spent cap would fail the anchors below loudly rather than silently.
    /// </summary>
    private const string ResolveInvitedOnAConflictedIntegration =
        "    To reconcile: choose 'resolve' — the server spawns ONE agent that reconciles these branches, builds, and runs the tests, then you merge again. Or stop to leave the conflict for a human.";

    /// <summary>
    /// One scenario's prompt wound back to the rendering that produced the superseded pins below: the turn's VERB
    /// ROSTER replaced by the action mask it grew out of, and the conflicted-integration block's cap-aware closing
    /// line replaced by the invitation it retired. It exists because the roster renders on EVERY turn where the mask
    /// rendered on most, so a superseded digest recomputed over today's raw rendering can no longer reproduce
    /// itself, and both receipts below would have to be deleted or re-pinned into tautologies.
    ///
    /// <para>Winding them back keeps the receipts, and makes them stronger than a re-pin would: the roster is a pure
    /// INSERTION over the mask and the closing line is a pure SUBSTITUTION, so undoing exactly those two must return
    /// the pre-commit bytes — which is what the anchors assert by still reproducing their old digests over the whole
    /// pre-pin corpus. Anything else that drifted into this commit shows up as a failure here rather than as a
    /// digest nobody can attribute. Every part except the deleted line is taken FROM the renderers rather than
    /// retyped, in the same spirit as the stage line below.</para>
    /// </summary>
    private static string AsRenderedBeforeTheTurnRoster(string prompt, SupervisorTurnContext context)
    {
        var roster = $"{Environment.NewLine}{SupervisorActionRoster.Render(context)}{Environment.NewLine}";
        var mask = SupervisorActionMask.Render(context) is { } withheld ? $"{Environment.NewLine}{withheld}{Environment.NewLine}" : string.Empty;

        return prompt
            .Replace(roster, mask, StringComparison.Ordinal)
            .Replace(LlmSupervisorDecider.ResolveWithdrawnOnAConflictedIntegration, ResolveInvitedOnAConflictedIntegration, StringComparison.Ordinal);
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
