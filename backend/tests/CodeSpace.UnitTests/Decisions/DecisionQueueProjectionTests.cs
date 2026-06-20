using System.Text.Json;
using CodeSpace.Core.Services.Decisions;
using CodeSpace.Messages.Decisions;
using Shouldly;

namespace CodeSpace.UnitTests.Decisions;

/// <summary>
/// Pins the ONE shared projection the cross-grain decision queue (D3) funnels both backends through
/// (<see cref="DecisionQueueService.Project"/>): a stashed <see cref="DecisionRequest"/> envelope maps to a
/// <see cref="Messages.Dtos.Decisions.PendingDecision"/> identically regardless of grain, and a missing / malformed /
/// incomplete envelope degrades to null (skipped) so a single bad row can never crash the queue.
/// </summary>
[Trait("Category", "Unit")]
public class DecisionQueueProjectionTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Guid RowId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Created = DateTimeOffset.UnixEpoch;

    [Theory]
    [InlineData(DecisionResumeBackends.ToolLedger)]
    [InlineData(DecisionResumeBackends.WorkflowWait)]
    public void A_valid_envelope_projects_identically_for_either_grain(string grain)
    {
        var deadline = DateTimeOffset.UnixEpoch.AddHours(1);
        var envelope = new DecisionRequest
        {
            Id = Guid.NewGuid(),
            RootTraceId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            AgentRunId = grain == DecisionResumeBackends.ToolLedger ? RowId : null,
            WorkflowRunId = grain == DecisionResumeBackends.WorkflowWait ? Guid.NewGuid() : null,
            NodeId = grain == DecisionResumeBackends.WorkflowWait ? "decide" : null,
            Scope = DecisionScopes.Agent,
            RequesterType = DecisionRequesterTypes.Agent,
            DecisionType = DecisionTypes.ChooseOne,
            Question = "Which path?",
            Options = new[] { new DecisionOption { Id = "a", Label = "A" }, new DecisionOption { Id = "b", Label = "B", IsSideEffecting = true } },
            RecommendedOption = "a",
            BlockingReason = "the schema diverged",
            RiskLevel = DecisionRiskLevels.High,
            Policy = DecisionPolicies.HumanRequired,
            TimeoutAt = deadline,
            DedupeKey = "k",
            ResumeBackend = grain,
        };

        var msgId = Guid.NewGuid();
        var projected = DecisionQueueService.Project(RowId, Created, msgId, JsonSerializer.Serialize(envelope, Json));

        projected.ShouldNotBeNull();
        projected!.Id.ShouldBe(RowId, "the queue handle is the ROW id, not the envelope id");
        projected.Grain.ShouldBe(grain);
        projected.RootTraceId.ShouldBe(envelope.RootTraceId);
        projected.Question.ShouldBe("Which path?");
        projected.Options.Count.ShouldBe(2);
        projected.Options[1].IsSideEffecting.ShouldBeTrue();
        projected.RecommendedOption.ShouldBe("a");
        projected.BlockingReason.ShouldBe("the schema diverged");
        projected.RiskLevel.ShouldBe(DecisionRiskLevels.High);
        projected.Policy.ShouldBe(DecisionPolicies.HumanRequired);
        projected.DeadlineAt.ShouldBe(deadline);
        projected.CreatedAt.ShouldBe(Created);
        projected.AnswerMessageId.ShouldBe(msgId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{ \"question\": \"missing required fields\" }")]   // required init members absent → STJ throws → skipped
    [InlineData("\"a-bare-string\"")]
    public void A_missing_or_malformed_envelope_is_skipped_not_crashed(string? envelopeJson)
    {
        DecisionQueueService.Project(RowId, Created, answerMessageId: null, envelopeJson).ShouldBeNull();
    }
}
