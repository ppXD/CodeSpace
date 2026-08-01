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
