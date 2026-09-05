using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the outcome folds must be ADDITIVE. Each fold runs post-barrier over an outcome another code path
/// authored, so re-emitting a FIXED shape silently deletes whatever that author wrote — which is exactly what
/// happened to a retry's <c>escalation</c> object: written at spawn time, erased by the agent-results fold, and
/// therefore never visible to its only two consumers (<c>LlmSupervisorDecider</c> and <c>SupervisorRecitation</c>),
/// both of which read the REHYDRATED tape rather than the executor's return value. Every existing test read the
/// outcome PRE-fold, so nothing went red.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorOutcomeFoldTests
{
    private static readonly IReadOnlyList<SupervisorAgentResult> OneResult =
        new[] { new SupervisorAgentResult { AgentRunId = Guid.Parse("11111111-1111-1111-1111-111111111111"), Status = "Succeeded" } };

    /// <summary>The exact shape RealSupervisorActionExecutor's retry path records: the staged ids plus an escalation object.</summary>
    private static string SpawnOutcomeWithEscalation(Guid agentRunId) =>
        JsonSerializer.Serialize(new
        {
            agentRunIds = new[] { agentRunId },
            agentCount = 1,
            escalation = new { from = "weak-model", to = "strong-model", reason = "the first attempt failed its acceptance" },
        }, AgentJson.Options);

    [Fact]
    public void The_agent_results_fold_preserves_the_escalation_the_decider_reads()
    {
        var agentRunId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        var folded = SupervisorOutcome.FoldAgentResults(SpawnOutcomeWithEscalation(agentRunId), OneResult);

        var escalation = SupervisorOutcome.ReadEscalation(folded);

        escalation.ShouldNotBeNull("the fold runs BEFORE the decider reads the tape — dropping the escalation here means it never existed as far as the run is concerned");
        escalation!.To.ShouldBe("strong-model");
        escalation.From.ShouldBe("weak-model");
        escalation.Reason.ShouldBe("the first attempt failed its acceptance");
    }

    [Fact]
    public void The_agent_results_fold_still_records_what_it_always_recorded()
    {
        var agentRunId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        var folded = SupervisorOutcome.FoldAgentResults(SpawnOutcomeWithEscalation(agentRunId), OneResult);

        SupervisorOutcome.ReadStagedAgentRunIds(folded).ShouldBe(new[] { agentRunId }, "the E5 counters read these off the folded tape");
        SupervisorOutcome.ReadStagedAgentCount(folded).ShouldBe(1);
        SupervisorOutcome.ReadAgentResults(folded).Count.ShouldBe(1, "the whole point of the fold");
    }

    [Fact]
    public void The_agent_results_fold_is_idempotent()
    {
        var once = SupervisorOutcome.FoldAgentResults(SpawnOutcomeWithEscalation(Guid.Parse("11111111-1111-1111-1111-111111111111")), OneResult);
        var twice = SupervisorOutcome.FoldAgentResults(once, OneResult);

        twice.ShouldBe(once, "the rehydrate persist no-ops only if re-folding is byte-stable — a merge that appended or reordered would write on every replay");
    }

    [Fact]
    public void A_zero_agent_outcome_is_still_returned_untouched()
    {
        const string zeroAgent = """{"agentRunIds":[],"agentCount":0,"note":"no subtasks to spawn"}""";

        SupervisorOutcome.FoldAgentResults(zeroAgent, Array.Empty<SupervisorAgentResult>())
            .ShouldBe(zeroAgent, "a zero-agent spawn keeps its own shape verbatim — this arm was always correct and must not start rewriting");
    }

    [Fact]
    public void The_acceptance_grade_fold_also_preserves_keys_it_does_not_own()
    {
        var resolveOutcome = JsonSerializer.Serialize(new
        {
            agentRunIds = new[] { Guid.Parse("22222222-2222-2222-2222-222222222222") },
            agentCount = 1,
            escalation = new { from = "a", to = "b", reason = "r" },
        }, AgentJson.Options);

        var folded = SupervisorOutcome.FoldAcceptanceGrade(resolveOutcome, passed: true, detail: "green");

        SupervisorOutcome.ReadAcceptanceGradePassed(folded).ShouldBe(true);
        SupervisorOutcome.ReadEscalation(folded).ShouldNotBeNull("the resolve fold re-emitted a fixed four-key shape too — same defect, same fix");
    }

    [Fact]
    public void The_payload_re_ask_fold_round_trips_and_preserves_the_keys_it_does_not_own()
    {
        var folded = SupervisorOutcome.WritePayloadReask(SpawnOutcomeWithEscalation(Guid.Parse("33333333-3333-3333-3333-333333333333")), SupervisorDecisionKinds.Plan);

        SupervisorOutcome.ReadPayloadReaskedFromKind(folded).ShouldBe(SupervisorDecisionKinds.Plan);
        folded.ShouldContain("\"payloadReasked\":true", customMessage: "the flag a reader scans for is written explicitly, not inferred from the kind's presence");
        SupervisorOutcome.ReadEscalation(folded).ShouldNotBeNull("every fold here runs post-barrier over an outcome someone else authored");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_decision_that_needed_no_re_ask_is_left_byte_identical(string? noReask)
    {
        const string outcome = """{"agentCount":1}""";

        SupervisorOutcome.WritePayloadReask(outcome, noReask).ShouldBe(outcome, "the overwhelmingly common path must not touch the outcome at all");
        SupervisorOutcome.ReadPayloadReaskedFromKind(outcome).ShouldBeNull();
        SupervisorOutcome.ReadPayloadReaskedFromKind("not json").ShouldBeNull();
        SupervisorOutcome.ReadPayloadReaskedFromKind(null).ShouldBeNull();
    }

    [Fact]
    public void The_retry_target_re_ask_fold_round_trips_and_preserves_the_keys_it_does_not_own()
    {
        var folded = SupervisorOutcome.WriteRetryTargetReask(SpawnOutcomeWithEscalation(Guid.Parse("44444444-4444-4444-4444-444444444444")), reasked: true);

        SupervisorOutcome.ReadRetryTargetReasked(folded).ShouldBeTrue();
        folded.ShouldContain("\"retryTargetReasked\":true");
        SupervisorOutcome.ReadEscalation(folded).ShouldNotBeNull("every fold here runs post-barrier over an outcome someone else authored");
    }

    [Fact]
    public void A_retry_the_model_aimed_correctly_first_time_is_left_byte_identical()
    {
        const string outcome = """{"agentCount":1}""";

        SupervisorOutcome.WriteRetryTargetReask(outcome, reasked: false).ShouldBe(outcome, "the overwhelmingly common path must not touch the outcome at all");
        SupervisorOutcome.ReadRetryTargetReasked(outcome).ShouldBeFalse();
        SupervisorOutcome.ReadRetryTargetReasked("not json").ShouldBeFalse();
        SupervisorOutcome.ReadRetryTargetReasked(null).ShouldBeFalse();
    }
}
