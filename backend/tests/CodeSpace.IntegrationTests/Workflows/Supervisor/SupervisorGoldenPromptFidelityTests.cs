using CodeSpace.Core.Services.Supervisor.Deciders;
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
