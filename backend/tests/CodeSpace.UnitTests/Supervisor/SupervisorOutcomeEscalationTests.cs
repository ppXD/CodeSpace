using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Supervisor;

/// <summary>
/// A2 (P4-2) — the two pure reads a retry's tier escalation needs off the tape: <see cref="SupervisorOutcome.FindResultByAgentRunId"/>
/// (what did the unit being retried actually do last time) and <see cref="SupervisorOutcome.ReadEscalation"/> (what did
/// THIS retry decision itself record about raising the floor).
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorOutcomeEscalationTests
{
    [Fact]
    public void FindResultByAgentRunId_finds_a_result_folded_under_an_earlier_decision()
    {
        var target = Guid.NewGuid();
        var other = Guid.NewGuid();

        var priors = new[]
        {
            Spawn(1, new SupervisorAgentResult { AgentRunId = other, Status = "Succeeded" }),
            Spawn(2, new SupervisorAgentResult { AgentRunId = target, Status = "Failed", Contradiction = "over_claim", Model = "claude-haiku-4-5" }),
        };

        var found = SupervisorOutcome.FindResultByAgentRunId(priors, target);

        found.ShouldNotBeNull();
        found!.Contradiction.ShouldBe("over_claim");
        found.Model.ShouldBe("claude-haiku-4-5");
    }

    [Fact]
    public void FindResultByAgentRunId_returns_null_for_a_null_id() =>
        SupervisorOutcome.FindResultByAgentRunId(new[] { Spawn(1, new SupervisorAgentResult { AgentRunId = Guid.NewGuid(), Status = "Succeeded" }) }, null).ShouldBeNull();

    [Fact]
    public void FindResultByAgentRunId_returns_null_when_never_staged() =>
        SupervisorOutcome.FindResultByAgentRunId(Array.Empty<SupervisorPriorDecision>(), Guid.NewGuid()).ShouldBeNull();

    [Fact]
    public void ReadEscalation_reads_the_full_block()
    {
        const string outcome = """{"agentRunIds":["a"],"agentCount":1,"escalation":{"from":"claude-haiku-4-5","to":"claude-sonnet-4-5","reason":"the prior attempt's self-report contradicted its acceptance grade (over_claim)"}}""";

        var escalation = SupervisorOutcome.ReadEscalation(outcome);

        escalation.ShouldNotBeNull();
        escalation!.From.ShouldBe("claude-haiku-4-5");
        escalation.To.ShouldBe("claude-sonnet-4-5");
        escalation.Reason.ShouldBe("the prior attempt's self-report contradicted its acceptance grade (over_claim)");
    }

    [Fact]
    public void ReadEscalation_tolerates_a_null_from_when_the_prior_model_was_unknown()
    {
        const string outcome = """{"agentRunIds":["a"],"agentCount":1,"escalation":{"to":"claude-sonnet-4-5","reason":"one away from the no-progress cap"}}""";

        var escalation = SupervisorOutcome.ReadEscalation(outcome);

        escalation.ShouldNotBeNull();
        escalation!.From.ShouldBeNull();
        escalation.To.ShouldBe("claude-sonnet-4-5");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("""{"agentRunIds":["a"],"agentCount":1,"escalation":null}""")]
    [InlineData("""{"agentRunIds":["a"],"agentCount":1}""")]
    [InlineData("""{"agentRunIds":["a"],"agentCount":1,"escalation":{"from":"x"}}""")]
    public void ReadEscalation_reads_null_when_absent_or_malformed(string? outcomeJson) =>
        SupervisorOutcome.ReadEscalation(outcomeJson).ShouldBeNull();

    private static SupervisorPriorDecision Spawn(long seq, params SupervisorAgentResult[] results) => new()
    {
        Id = Guid.NewGuid(),
        Sequence = seq,
        Status = SupervisorDecisionStatus.Succeeded,
        DecisionKind = SupervisorDecisionKinds.Spawn,
        PayloadJson = "{}",
        OutcomeJson = JsonSerializer.Serialize(new { agentRunIds = results.Select(r => r.AgentRunId), agentCount = results.Length, agentResults = results }, AgentJson.Options),
    };
}
