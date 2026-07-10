using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Supervisor;

/// <summary>
/// DC-2 — <see cref="SupervisorOutcome.RejectedAgentRunIds"/> (the run-wide rejected-contributor set the ledger-direct
/// branch resolver excludes) and <see cref="SupervisorOutcome.ReadPublishAttempt"/> (the gate's re-check of a prior
/// <c>publish</c> decision's own recorded outcome).
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorOutcomePublishTests
{
    [Fact]
    public void RejectedAgentRunIds_collects_every_rejected_contributor_across_the_whole_tape()
    {
        var rejected = Guid.NewGuid();
        var accepted = Guid.NewGuid();

        var decisions = new[]
        {
            Decision(SupervisorDecisionKinds.Spawn, 1, ResultsOutcome((rejected, false), (accepted, true))),
            Decision(SupervisorDecisionKinds.Retry, 2, ResultsOutcome((accepted, null))),
        };

        var result = SupervisorOutcome.RejectedAgentRunIds(decisions);

        result.ShouldContain(rejected);
        result.ShouldNotContain(accepted);
    }

    [Fact]
    public void RejectedAgentRunIds_is_empty_when_nothing_was_ever_rejected() =>
        SupervisorOutcome.RejectedAgentRunIds(Array.Empty<SupervisorPriorDecision>()).ShouldBeEmpty();

    [Fact]
    public void ReadPublishAttempt_reports_success_when_at_least_one_pr_opened()
    {
        const string outcome = """{"publish":{"opened":[{"alias":"primary","url":"https://x","number":1}],"failed":[]}}""";

        var attempt = SupervisorOutcome.ReadPublishAttempt(outcome);

        attempt.ShouldNotBeNull();
        attempt!.AnySucceeded.ShouldBeTrue();
        attempt.Reasons.ShouldBeEmpty();
    }

    [Fact]
    public void ReadPublishAttempt_reports_failure_and_names_every_reason_when_nothing_opened()
    {
        const string outcome = """{"publish":{"opened":[],"failed":[{"alias":"primary","error":"the provider rate-limited the request"},{"alias":"secondary","error":"403"}]}}""";

        var attempt = SupervisorOutcome.ReadPublishAttempt(outcome);

        attempt!.AnySucceeded.ShouldBeFalse();
        attempt.Reasons.ShouldBe(new[] { "the provider rate-limited the request", "403" });
    }

    [Fact]
    public void ReadPublishAttempt_treats_a_partial_success_as_succeeded()
    {
        const string outcome = """{"publish":{"opened":[{"alias":"frontend","url":"https://x","number":1}],"failed":[{"alias":"backend","error":"403"}]}}""";

        SupervisorOutcome.ReadPublishAttempt(outcome)!.AnySucceeded.ShouldBeTrue("at least one repo delivered — the same isolation ChangeSetService itself applies");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"merged":[],"count":0}""")]
    public void ReadPublishAttempt_reads_null_when_absent_or_malformed(string? outcomeJson) =>
        SupervisorOutcome.ReadPublishAttempt(outcomeJson).ShouldBeNull();

    [Fact]
    public void Publish_never_stages_agents_and_always_closes_its_turn()
    {
        SupervisorDecisionKinds.StagesAgents(SupervisorDecisionKinds.Publish).ShouldBeFalse("publish opens a PR, it never creates an agent run");
        SupervisorDecisionKinds.ClosesTurn(SupervisorDecisionKinds.Publish).ShouldBeTrue("publish is synchronous and self-advances, exactly like merge");
    }

    private static SupervisorPriorDecision Decision(string kind, long seq, string outcomeJson) =>
        new() { Id = Guid.NewGuid(), Sequence = seq, DecisionKind = kind, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}", OutcomeJson = outcomeJson };

    private static string ResultsOutcome(params (Guid AgentRunId, bool? AcceptancePassed)[] results)
    {
        var agentResults = results.Select(r => new SupervisorAgentResult { AgentRunId = r.AgentRunId, Status = "Succeeded", AcceptancePassed = r.AcceptancePassed }).ToArray();

        return System.Text.Json.JsonSerializer.Serialize(new { agentRunIds = agentResults.Select(r => r.AgentRunId), agentCount = agentResults.Length, agentResults }, CodeSpace.Core.Services.Agents.AgentJson.Options);
    }
}
