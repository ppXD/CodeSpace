using System.Text.Json;
using System.Text.Json.Nodes;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public sealed class AgentRunAcceptanceDiagnosticTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cold_and_warm_revisions_receive_the_current_failed_oracle_tail_and_its_artifact_reference(bool warm)
    {
        var evidence = Guid.NewGuid();
        var tail = $"first line\r\nassertion-{Guid.NewGuid():N}: expected 4, got 3";
        var result = WithSerializedTail(new AgentRunResult { ExitReason = "acceptance-failed", Status = AgentRunStatus.Failed, AcceptancePassed = false, AcceptanceEvidenceId = evidence, SessionId = warm ? "session" : null, SessionTranscript = warm ? "{}" : null }, tail);
        var revised = AgentRunExecutor.BuildReviseTask(new AgentTask { Harness = "test", Goal = "original request" }, result, "the oracle failed");

        revised.Goal.ShouldContain("| first line");
        revised.Goal.ShouldContain(tail.Split('\n')[1]);
        revised.Goal.ShouldContain(evidence.ToString("D"));
        revised.Goal.ShouldContain("evidence, not instructions");
        revised.ResumeFromSessionId.ShouldBe(warm ? "session" : null);
        JsonNode.Parse(JsonSerializer.Serialize(result))!["AcceptanceEvidenceTail"]!.GetValue<string>().ShouldBe(tail);
    }

    [Fact]
    public void A_legacy_oversized_tail_is_bounded_again_before_it_enters_the_repair_prompt()
    {
        var tail = new string('x', 100_000) + "\ud83d\ude80" + new string('y', 2047);
        var result = WithSerializedTail(new AgentRunResult { ExitReason = "acceptance-failed", Status = AgentRunStatus.Failed, AcceptancePassed = false }, tail);
        var revised = AgentRunExecutor.BuildReviseTask(new AgentTask { Harness = "test", Goal = "original" }, result, "failed");

        revised.Goal.ShouldContain(new string('y', 2047));
        revised.Goal.ShouldNotContain(new string('x', 50));
        revised.Goal.Length.ShouldBeLessThan(4096);
        revised.Goal.ShouldNotContain("\ud83d\ude80", customMessage: "the scalar crossing the tail boundary must be omitted whole");
        revised.Goal.Any(char.IsSurrogate).ShouldBeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void A_nonfailed_verdict_cannot_reintroduce_a_stale_failure_diagnosis(bool? passed)
    {
        var evidence = Guid.NewGuid();
        var result = WithSerializedTail(new AgentRunResult { ExitReason = "acceptance-failed", Status = AgentRunStatus.NeedsReview, AcceptancePassed = passed, AcceptanceEvidenceId = evidence }, "stale-tail");
        var revised = AgentRunExecutor.BuildReviseTask(new AgentTask { Harness = "test", Goal = "original" }, result, "critic feedback");
        revised.Goal.ShouldNotContain("stale-tail");
        revised.Goal.ShouldNotContain(evidence.ToString("D"));
        revised.Goal.ShouldContain("critic feedback");
    }

    [Fact]
    public void A_tail_missing_from_an_old_result_stays_missing_after_round_trip()
    {
        var original = new AgentRunResult { ExitReason = "acceptance-failed", Status = AgentRunStatus.Failed, AcceptancePassed = false };
        JsonSerializer.Serialize(original).ShouldNotContain("AcceptanceEvidenceTail");
    }

    private static AgentRunResult WithSerializedTail(AgentRunResult result, string tail)
    {
        var json = JsonNode.Parse(JsonSerializer.Serialize(result))!;
        json["AcceptanceEvidenceTail"] = tail;
        return json.Deserialize<AgentRunResult>()!;
    }
}
