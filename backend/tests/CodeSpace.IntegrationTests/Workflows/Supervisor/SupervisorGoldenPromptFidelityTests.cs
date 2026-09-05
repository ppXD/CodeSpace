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
        "agent-reported-conflict-no-integration", "all-failed", "all-succeeded", "five-subtask-middle-failed",
        "four-subtask-all-succeeded", "four-subtask-two-failed", "merge-conflict", "mixed-results",
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
    /// </summary>
    [Fact]
    public void Only_a_scenario_missing_a_required_stage_renders_a_different_prompt_than_before()
    {
        var profile = new ModeProfileRegistry().Resolve(RunModeKeys.Supervisor)!;
        var moved = new List<string>();

        foreach (var scenario in SupervisorDecisionGoldenScenarios.All)
        {
            var projected = SupervisorTapeCompletion.ProjectIfStoppedNow(scenario.Context.PriorDecisions);

            // The rendering this corpus shipped BEFORE the mirror carried the trace: dimensions only, no profile.
            var dimensionsOnly = SupervisorStopNowRecital.Render(projected?.Assessment);
            var withTrace = scenario.Context.CompletionRecital;

            var before = LlmSupervisorDecider.BuildUserPromptForTest(scenario.Context with { CompletionRecital = dimensionsOnly });
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
    /// </summary>
    private const string GoldenPromptDigest = "9a06aec3056ee4851e8ccd69cdb67585b6b3f20a4414ec03be2dc0ea188426ba";

    [Fact]
    public void The_rendered_corpus_matches_its_pinned_digest()
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RenderedCorpus()))).ToLowerInvariant();

        digest.ShouldBe(GoldenPromptDigest,
            $"the rendered golden prompts changed. If that was intended, re-pin GoldenPromptDigest to '{digest}' and name the block that changed in the commit body; if it was not, a decider edit has silently moved what every real-model score measures.");
    }

    [Fact]
    public void The_digest_covers_every_scenario_in_the_corpus()
    {
        // A digest over a shrinking corpus is a green light for a shrinking corpus. Pin the count beside the bytes.
        SupervisorDecisionGoldenScenarios.All.Count.ShouldBe(23, "a scenario was added or dropped — re-pin this count together with the digest");
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
    private static string RenderedCorpus()
    {
        var builder = new StringBuilder();

        foreach (var scenario in SupervisorDecisionGoldenScenarios.All.OrderBy(s => s.Name, StringComparer.Ordinal))
            builder.Append("\u0000").Append(scenario.Name).Append("\u0000").Append(LlmSupervisorDecider.BuildUserPromptForTest(scenario.Context));

        return builder.ToString();
    }

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
