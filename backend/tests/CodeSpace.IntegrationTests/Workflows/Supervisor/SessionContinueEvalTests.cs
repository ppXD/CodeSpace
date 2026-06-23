using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// Always-on (no model, no Postgres) self-test of the SESSION CONTEXT-HANDOFF rubric — proves the gate has TEETH
/// before any real-model decision is replayed through it: (a) every continuing-turn scenario's goal actually carries
/// the prior-turn digest + the continue framing (so the handoff reaches the brain's prompt at all), and (b) the
/// rubric REJECTS a context-blind decision (re-planning already-shipped work; a plan that ignores the new ask) and
/// PASSES the context-aware one. The real-model replay (<c>RealModelSessionContinueFlowTests</c>) scores the live
/// brain through this same verified-honest rubric.
/// </summary>
[Trait("Category", "Integration")]
public class SessionContinueEvalTests
{
    [Fact]
    public void Every_continue_scenario_goal_carries_the_prior_digest_and_the_continue_framing()
    {
        foreach (var scenario in SessionContinueGoldenScenarios.All)
        {
            scenario.Context.Goal.ShouldContain("Earlier turns in this work thread",
                customMessage: $"{scenario.Name}: the prior-turn digest must be folded into the brain's prompt");
            scenario.Context.Goal.ShouldContain("do not start over",
                customMessage: $"{scenario.Name}: the continue framing (build on prior work) must be present");
        }
    }

    [Fact]
    public void Redundant_continue_rejects_redoing_already_shipped_work()
    {
        var redundant = SessionContinueGoldenScenarios.All.Single(s => s.Name == "continue-redundant-complete");

        SupervisorDecisionEval.Score(redundant, Decision(SupervisorDecisionKinds.Plan)).Pass
            .ShouldBeFalse("re-planning work the digest says is already shipped ignores the handoff");
        SupervisorDecisionEval.Score(redundant, Decision(SupervisorDecisionKinds.Spawn)).Pass
            .ShouldBeFalse("spawning to redo already-shipped work ignores the handoff");
        SupervisorDecisionEval.Score(redundant, Decision(SupervisorDecisionKinds.Stop)).Pass
            .ShouldBeTrue("recognising the work is already done is the context-aware move");
    }

    [Fact]
    public void Incremental_continue_requires_the_plan_to_address_the_new_ask()
    {
        var incremental = SessionContinueGoldenScenarios.All.Single(s => s.Name == "continue-incremental");

        SupervisorDecisionEval.Score(incremental, Plan(("s1", "Add email validation", "re-add email-format validation to the signup endpoint"))).Pass
            .ShouldBeFalse("a plan that redoes the prior (done) work did not read the new follow-up ask");
        SupervisorDecisionEval.Score(incremental, Plan(("s1", "Add rate limiting", "add per-IP rate limiting of 5 requests/minute returning HTTP 429"))).Pass
            .ShouldBeTrue("a plan addressing the new rate-limiting ask passes");
    }

    private static SupervisorDecision Decision(string kind) => new() { Kind = kind, PayloadJson = "{}" };

    private static SupervisorDecision Plan(params (string Id, string Title, string Instruction)[] subtasks) => new()
    {
        Kind = SupervisorDecisionKinds.Plan,
        PayloadJson = JsonSerializer.Serialize(new { subtasks = subtasks.Select(s => new { id = s.Id, title = s.Title, instruction = s.Instruction }) }, AgentJson.Options),
    };
}
