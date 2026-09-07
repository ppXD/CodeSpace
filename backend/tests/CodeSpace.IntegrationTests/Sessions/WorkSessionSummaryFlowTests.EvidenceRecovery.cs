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

    [Fact]
    [Trait("P17", "Regression")]
    public async Task A_pre_migration_turn_with_no_stored_binding_is_backfilled_on_the_next_fold_and_carried_forward()
    {
        // A row from before summary_source_binding_jsonb existed: Summary + SummaryThroughTurnIndex already advanced
        // (under the OLD code), but SummarySourceBindingJson is NULL — no binding was ever recorded for turn 1, even
        // though it carries an unresolved assessment.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "claude-test");
        var sessionId = await SeedSessionAsync(teamId);
        for (var turn = 1; turn <= 9; turn++) await SeedTurnAsync(teamId, sessionId, turn, $"goal-{turn}", "The model claims all work is complete.");
        var (runId, assessmentId) = await AddUnresolvedAssessmentAsync(teamId, sessionId);
        await SetSummaryAsync(sessionId, "Legacy summary text folded before source bindings existed.", throughTurn: 1);

        // A new turn scrolls another older turn out of the window, so the summarizer has something to fold and runs.
        await SeedTurnAsync(teamId, sessionId, 10, "goal-10", "Other work.");
        await RunSummarizerAsync(teamId, sessionId, new CapturingLlmClient { Return = "Turns 1 and 2, folded." });

        var digest = await BuildDigestAsync(sessionId, teamId);
        digest.ShouldNotBeNull();
        digest.ShouldContain("UNRESOLVED CONTRACT", customMessage: "the legacy (never-bound) turn's unresolved assessment must be backfilled into a binding and carried forward — not lost forever because the incremental fold only touched turn 2");
        digest.ShouldContain("verification=Failed");
        digest.ShouldContain(runId.ToString(), customMessage: "a recovered fact must still identify its effective source run");
        digest.ShouldContain(assessmentId.ToString(), customMessage: "the raw historical assessment remains addressable");
    }

    [Fact]
    [Trait("P17", "Regression")]
    public async Task Carried_forward_block_flags_a_bound_verdict_when_a_newer_assessment_now_exists_for_the_run()
    {
        // SessionSummarizer's drift refresh is fail-open — a newer assessment does not always trigger a re-fold
        // before the NEXT digest is built. Swapping "the bound id" for "whatever is latest" would go undetected by
        // every OTHER assertion in this file (both would show the newest verdict) — this test pins that the digest
        // keeps showing the id the fold actually saw, with a flag, rather than silently upgrading to the newest one.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "claude-test");
        var sessionId = await SeedSessionAsync(teamId);
        for (var turn = 1; turn <= 8; turn++) await SeedTurnAsync(teamId, sessionId, turn, $"goal-{turn}", "result");
        var (runId, firstAssessmentId) = await AddUnresolvedAssessmentAsync(teamId, sessionId);

        // Fold turn 1 out of the window — its binding now carries the FIRST (unresolved) assessment.
        await SeedTurnAsync(teamId, sessionId, 9, "goal-9", "result-9");
        await RunSummarizerAsync(teamId, sessionId, new CapturingLlmClient { Return = "folded" });

        var beforeSupersession = await BuildDigestAsync(sessionId, teamId);
        beforeSupersession.ShouldContain(firstAssessmentId.ToString());
        beforeSupersession.ShouldNotContain("NEWER assessment", customMessage: "sanity: nothing is superseded yet");

        // A SECOND, newer assessment is recorded for the SAME run — the summarizer does NOT run again (a resumed
        // session is not the only way a digest gets rebuilt).
        var secondAssessmentId = await AddResolvedAssessmentAsync(teamId, runId);

        var digest = await BuildDigestAsync(sessionId, teamId);

        digest.ShouldContain(firstAssessmentId.ToString(), customMessage: "the BOUND (fold-time) assessment id is still what renders — never silently swapped for 'whatever is latest'");
        digest.ShouldContain("NEWER assessment", customMessage: "a newer assessment for the same run must be flagged, not presented as an unqualified current verdict");
        digest.ShouldNotContain(secondAssessmentId.ToString(), customMessage: "the new assessment's own id is not rendered — only the bound one, with a flag");
    }

    [Fact]
    [Trait("P17", "Regression")]
    public async Task Carried_forward_block_recovers_a_turn_folded_with_no_assessment_once_one_is_recorded()
    {
        // Turn 1 folds out of the window BEFORE its run has ever been assessed — its binding is written with
        // AssessmentId = null. The FIRST assessment for that run then lands AFTER the fold, before the summarizer
        // runs again (a resumed session is not the only way a digest gets rebuilt). A null-bound turn must not be a
        // permanent blind spot for the launch(es) between that assessment landing and the next successful fold
        // (SessionSummarizer's own dirty-check self-heals the binding, but only starting the NEXT fold).
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "claude-test");
        var sessionId = await SeedSessionAsync(teamId);
        for (var turn = 1; turn <= 8; turn++) await SeedTurnAsync(teamId, sessionId, turn, $"goal-{turn}", "result");

        // Fold turn 1 out of the window while its run still has NO assessment — its binding's AssessmentId is null.
        await SeedTurnAsync(teamId, sessionId, 9, "goal-9", "result-9");
        await RunSummarizerAsync(teamId, sessionId, new CapturingLlmClient { Return = "folded" });

        var beforeAssessment = await BuildDigestAsync(sessionId, teamId);
        beforeAssessment.ShouldNotContain("UNRESOLVED CONTRACT", customMessage: "sanity: the run had no assessment when it was folded");

        var (runId, assessmentId) = await AddUnresolvedAssessmentAsync(teamId, sessionId);

        var digest = await BuildDigestAsync(sessionId, teamId);

        digest.ShouldContain("UNRESOLVED CONTRACT", customMessage: "a turn bound with no assessment must not stay a permanent blind spot once one is recorded for its run");
        digest.ShouldContain("verification=Failed");
        digest.ShouldContain(runId.ToString(), customMessage: "a recovered fact must still identify its effective source run");
        digest.ShouldContain(assessmentId.ToString(), customMessage: "the newly recorded assessment is addressable");
        digest.ShouldContain("[an assessment was recorded for this run AFTER the last fold]", customMessage: "recovered from a null binding (not the fold's own bound id) must be flagged as such, not presented as an unqualified fold-time verdict");
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

    /// <summary>Record a SECOND, later, RESOLVED assessment on an already-bound run — simulating a re-verification that landed after the fold, before the summarizer has refreshed the binding.</summary>
    private async Task<Guid> AddResolvedAssessmentAsync(Guid teamId, Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var id = Guid.NewGuid();
        db.CompletionAssessmentRecord.Add(new CompletionAssessmentRecord
        {
            Id = id, TeamId = teamId, WorkflowRunId = runId, EnforcementMode = "Shadow", Basis = "ContractDerived", Outcome = "Solved", Verification = "Passed",
            AssessmentJson = JsonSerializer.Serialize(new CompletionAssessment
            {
                Basis = CompletionBasis.ContractDerived, Execution = ExecutionDisposition.Completed, Outcome = OutcomeDisposition.Solved,
                Verification = VerificationDisposition.Passed, Artifact = ArtifactDisposition.Captured, Delivery = DeliveryDisposition.Delivered,
            }, AgentJson.Options), LegacyIsSolved = true, WouldBeTerminalDecision = null,
            CreatedDate = DateTimeOffset.UtcNow.AddSeconds(1),
        });
        await db.SaveChangesAsync();
        return id;
    }
}
