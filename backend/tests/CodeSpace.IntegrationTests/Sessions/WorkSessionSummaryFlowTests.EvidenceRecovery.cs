using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Context.Sources;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Sessions;

public partial class WorkSessionSummaryFlowTests
{
    [Fact]
    [Trait("P17", "Regression")]
    public async Task Unresolved_assessment_survives_multiple_compactions_and_fresh_scopes_after_turn_nine()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "claude-test");
        var sessionId = await SeedSessionAsync(teamId);
        for (var turn = 1; turn <= 8; turn++) await SeedTurnAsync(teamId, sessionId, turn, $"goal-{turn}", "The model claims all work is complete.");
        var (runId, assessmentId) = await AddUnresolvedAssessmentAsync(teamId, sessionId);
        (await BuildDigestAsync(sessionId, teamId)).ShouldContain("verification=Failed");

        for (var turn = 9; turn <= 17; turn++)
        {
            await SeedTurnAsync(teamId, sessionId, turn, $"goal-{turn}", "Other work.");
            await RunSummarizerAsync(teamId, sessionId, new CapturingLlmClient { Return = "All earlier work was completed and delivered." });
        }

        var digest = await BuildDigestAsync(sessionId, teamId);
        digest.ShouldNotBeNull();
        digest.ShouldContain("UNRESOLVED CONTRACT", customMessage: "a model summary cannot retire a persisted outstanding contract after its turn exits the recent window");
        digest.ShouldContain("verification=Failed");
        digest.ShouldContain("delivery=Unknown");
        digest.ShouldContain(runId.ToString(), customMessage: "a recorded fact must identify the effective source run");
        digest.ShouldContain(assessmentId.ToString(), customMessage: "the raw historical assessment remains addressable");
    }

    [Theory]
    [InlineData("effective-rerun")]
    [InlineData("source-result")]
    [InlineData("assessment")]
    [Trait("P17", "Regression")]
    public async Task A_source_change_behind_the_summary_watermark_is_observed_after_restart(string change)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "claude-test");
        var sessionId = await SeedSessionAsync(teamId);
        for (var turn = 1; turn <= 9; turn++) await SeedTurnAsync(teamId, sessionId, turn, $"goal-{turn}", "ORIGINAL_SOURCE");
        await RunSummarizerAsync(teamId, sessionId, new CapturingLlmClient { Return = "OLD_DERIVED_SUMMARY" });
        (await LoadSessionAsync(sessionId)).SummaryThroughTurnIndex.ShouldBe(1);

        using (var scope = _fixture.BeginScopeAs(userId, teamId))
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var original = await db.WorkflowRun.SingleAsync(r => r.SessionId == sessionId && r.SessionTurnIndex == 1);
            if (change == "effective-rerun")
            {
                original.Status = WorkflowRunStatus.Failure;
                var requestId = Guid.NewGuid();
                var now = DateTimeOffset.UtcNow;
                db.WorkflowRunRequest.Add(new WorkflowRunRequest
                {
                    Id = requestId, TeamId = teamId, SourceType = WorkflowRunSourceTypes.Rerun, ActorType = WorkflowRunActorTypes.User,
                    ActorId = userId, NormalizedPayloadJson = JsonSerializer.Serialize(new { goal = "goal-1" }), Status = WorkflowRunRequestStatus.Consumed,
                    ReceivedAt = now, VerifiedAt = now, NormalizedAt = now,
                });
                db.WorkflowRun.Add(new WorkflowRun
                {
                    Id = Guid.NewGuid(), TeamId = teamId, RunRequestId = requestId, SourceType = WorkflowRunSourceTypes.Rerun,
                    Status = WorkflowRunStatus.Success, SessionId = sessionId, ParentRunId = original.Id, RootRunId = original.Id, RerunFromNodeId = "agent",
                    OutputsJson = JsonSerializer.Serialize(new { summary = "NEW_DURABLE_SOURCE" }), CreatedDate = now,
                    CreatedBy = userId, LastModifiedBy = userId,
                });
            }
            else if (change == "source-result") original.OutputsJson = JsonSerializer.Serialize(new { summary = "NEW_DURABLE_SOURCE" });
            await db.SaveChangesAsync();
        }
        if (change == "assessment") await AddUnresolvedAssessmentAsync(teamId, sessionId);

        var capture = new CapturingLlmClient { Return = "REFRESHED_DERIVED_SUMMARY" };
        await RunSummarizerAsync(teamId, sessionId, capture);

        capture.Called.ShouldBeTrue("unchanged turn index cannot assert freshness after its effective source identity/content/assessment changed");
        (await LoadSessionAsync(sessionId)).Summary.ShouldBe("REFRESHED_DERIVED_SUMMARY");
        if (change != "assessment") capture.LastUserPrompt.ShouldContain("NEW_DURABLE_SOURCE");
        (await LoadSessionAsync(sessionId)).SummaryThroughTurnIndex.ShouldBe(1, "a refresh does not invent a new turn");
    }

    [Fact]
    [Trait("P17", "Regression")]
    public async Task An_initial_failed_summary_exposes_missing_coverage_without_existing_prose()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "claude-test");
        var sessionId = await SeedSessionAsync(teamId);
        for (var turn = 1; turn <= 9; turn++) await SeedTurnAsync(teamId, sessionId, turn, $"goal-{turn}", "recorded result");
        await RunSummarizerAsync(teamId, sessionId, new ThrowingLlmClient());
        (await LoadSessionAsync(sessionId)).Summary.ShouldBeNull();
        (await LoadSessionAsync(sessionId)).SummaryStaleSinceTurn.ShouldBe(1);

        (await BuildDigestAsync(sessionId, teamId)).ShouldContain("have not yet been folded", customMessage: "the persisted gap must be visible even when there is no old prose to prepend");
        using var scope = _fixture.BeginScope();
        var pulled = await scope.Resolve<SessionSummaryContextSource>().RetrieveAsync(new AgentContextQuery { TeamId = teamId, SessionId = sessionId, RunId = Guid.NewGuid() }, CancellationToken.None);
        pulled.Found.ShouldBeTrue("a known coverage gap is context, not an empty success");
        pulled.Text.ShouldContain("have not yet been folded");
    }

    private async Task<(Guid RunId, Guid AssessmentId)> AddUnresolvedAssessmentAsync(Guid teamId, Guid sessionId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var runId = await db.WorkflowRun.Where(r => r.SessionId == sessionId && r.SessionTurnIndex == 1).Select(r => r.Id).SingleAsync();
        var id = Guid.NewGuid();
        db.CompletionAssessmentRecord.Add(new CompletionAssessmentRecord
        {
            Id = id, TeamId = teamId, WorkflowRunId = runId, EnforcementMode = "Shadow", Basis = "ContractDerived", Outcome = "Unsolved", Verification = "Failed",
            AssessmentJson = JsonSerializer.Serialize(new CompletionAssessment
            {
                Basis = CompletionBasis.ContractDerived, Execution = ExecutionDisposition.Completed, Outcome = OutcomeDisposition.Unsolved,
                Verification = VerificationDisposition.Failed, Artifact = ArtifactDisposition.Captured, Delivery = DeliveryDisposition.Unknown,
            }, AgentJson.Options), LegacyIsSolved = true, WouldBeTerminalDecision = "HonestFailure",
        });
        await db.SaveChangesAsync();
        return (runId, id);
    }
}
