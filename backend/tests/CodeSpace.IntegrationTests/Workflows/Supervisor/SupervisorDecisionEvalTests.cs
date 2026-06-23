using System.Text.Json;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// Always-on (no model, no Postgres) self-test of the <see cref="SupervisorDecisionEval"/> rubric — proves the
/// scorer has TEETH (a wrong kind / wrong payload fails) before any real-model decision is replayed through it, so
/// a recorded real decision is judged by a verified-honest gate. The full golden scenarios + the real-decider
/// replay (the kill-gate that arms on a recorded cassette) build on this same scorer.
/// </summary>
[Trait("Category", "Integration")]
public class SupervisorDecisionEvalTests
{
    private static SupervisorGoldenScenario Scenario(IReadOnlyList<string> accepted, Func<SupervisorDecision, (bool, string)>? check = null) => new()
    {
        Name = "test",
        Context = new SupervisorTurnContext { Goal = "g", TurnNumber = 0 },
        AcceptedKinds = accepted,
        PayloadCheck = check,
    };

    [Fact]
    public void Passes_when_the_kind_is_accepted_and_there_is_no_payload_check()
    {
        var score = SupervisorDecisionEval.Score(
            Scenario(new[] { SupervisorDecisionKinds.Plan }),
            new SupervisorDecision { Kind = SupervisorDecisionKinds.Plan, PayloadJson = "{}" });

        score.Pass.ShouldBeTrue();
        score.ActualKind.ShouldBe(SupervisorDecisionKinds.Plan);
    }

    [Fact]
    public void Fails_when_the_kind_is_not_in_the_accepted_set()
    {
        // The teeth that catch a brain that quits early (stop) when it should merge.
        var score = SupervisorDecisionEval.Score(
            Scenario(new[] { SupervisorDecisionKinds.Merge }),
            new SupervisorDecision { Kind = SupervisorDecisionKinds.Stop, PayloadJson = "{}" });

        score.Pass.ShouldBeFalse();
        score.Note.ShouldContain("not in the accepted set");
    }

    [Fact]
    public void Fails_when_the_payload_check_rejects_and_passes_when_it_holds()
    {
        // The mixed-results teeth: a retry must target the FAILED subtask (s2), not blindly retry s1.
        var scenario = Scenario(new[] { SupervisorDecisionKinds.Retry }, RetryTargets("s2"));

        SupervisorDecisionEval.Score(scenario, Retry("s1")).Pass.ShouldBeFalse("retrying the wrong subtask must fail");
        SupervisorDecisionEval.Score(scenario, Retry("s2")).Pass.ShouldBeTrue("retrying the failed subtask passes");
    }

    [Fact]
    public void A_fail_closed_no_decision_stop_never_passes_even_a_should_stop_scenario()
    {
        // The model produced GARBAGE → the decider degraded to a fail-closed 'no-decision' stop (it did not crash). Even
        // when the scenario ACCEPTS stop, this must FAIL — the model did not DECIDE to stop, it failed to produce a usable
        // decision. Closes the false-green where a non-conformant reply could pass a should-stop golden scenario.
        var nonConformant = new SupervisorDecision
        {
            Kind = SupervisorDecisionKinds.Stop,
            PayloadJson = JsonSerializer.Serialize(new SupervisorStopPayload { Outcome = SupervisorStopPayload.NonConformantOutcome, Summary = "no usable decision" }, CodeSpace.Core.Services.Agents.AgentJson.Options),
        };

        var score = SupervisorDecisionEval.Score(Scenario(new[] { SupervisorDecisionKinds.Stop }), nonConformant);

        score.Pass.ShouldBeFalse("a fail-closed 'no-decision' stop is a non-conformant reply, not a genuine stop decision — it must fail even a should-stop scenario");
        score.Note.ShouldContain("no conformant decision");

        // A GENUINE stop (a real terminal outcome) still PASSES a should-stop scenario — the fix rejects only the fail-closed signature.
        var genuineStop = new SupervisorDecision
        {
            Kind = SupervisorDecisionKinds.Stop,
            PayloadJson = JsonSerializer.Serialize(new SupervisorStopPayload { Outcome = "done", Summary = "goal met" }, CodeSpace.Core.Services.Agents.AgentJson.Options),
        };

        SupervisorDecisionEval.Score(Scenario(new[] { SupervisorDecisionKinds.Stop }), genuineStop).Pass.ShouldBeTrue("a genuine 'done' stop is a real decision and passes a should-stop scenario");
    }

    [Fact]
    public void Aggregate_is_the_kill_gate_only_all_passed_arms_it()
    {
        var mixed = new[]
        {
            new SupervisorDecisionScore { Scenario = "a", ActualKind = "plan", AcceptedKinds = new[] { "plan" }, Pass = true, Note = "ok" },
            new SupervisorDecisionScore { Scenario = "b", ActualKind = "stop", AcceptedKinds = new[] { "merge" }, Pass = false, Note = "x" },
        };

        var (passed, total, all) = SupervisorDecisionEval.Aggregate(mixed);
        passed.ShouldBe(1);
        total.ShouldBe(2);
        all.ShouldBeFalse("any failed scenario disarms the kill-gate");

        SupervisorDecisionEval.Aggregate(new[] { mixed[0] }).AllPassed.ShouldBeTrue("all-passed arms it");
        SupervisorDecisionEval.Aggregate(Array.Empty<SupervisorDecisionScore>()).AllPassed.ShouldBeFalse("an empty run never silently passes");
    }

    private static Func<SupervisorDecision, (bool, string)> RetryTargets(string expectedSubtaskId) => decision =>
    {
        var subtaskId = JsonDocument.Parse(decision.PayloadJson).RootElement.TryGetProperty("subtaskId", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
        return subtaskId == expectedSubtaskId ? (true, "ok") : (false, $"retry targeted '{subtaskId}', expected '{expectedSubtaskId}'");
    };

    private static SupervisorDecision Retry(string subtaskId) =>
        new() { Kind = SupervisorDecisionKinds.Retry, PayloadJson = JsonSerializer.Serialize(new { subtaskId }) };
}
