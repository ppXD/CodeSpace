using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Eval;
using CodeSpace.Core.Services.Learning;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Learning;

/// <summary>Real PostgreSQL proof that only exact, genuine-launch, north-star successes can qualify failure-derived advice.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class LessonQualificationFlowTests
{
    private readonly PostgresFixture _fixture;

    public LessonQualificationFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Two_exact_unattended_successes_qualify_a_candidate_and_replay_is_idempotent()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var lessonId = await SeedLessonAsync(teamId);
        var first = await SeedScoredExposureAsync(teamId, lessonId, headline: true);
        var second = await SeedScoredExposureAsync(teamId, lessonId, headline: true);

        var concurrent = await Task.WhenAll(QualifyAsync(), QualifyAsync());
        concurrent.Sum().ShouldBeGreaterThanOrEqualTo(1, "concurrent sweepers serialize and converge on one durable evidence set");
        await QualifyAsync();

        var lesson = await ReadLessonAsync(lessonId);
        lesson.QualifiedAt.ShouldNotBeNull();
        lesson.SuccessfulExposureRunIds.ShouldBe(new[] { first, second }, ignoreOrder: true);
        lesson.NegativeExposureRunIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task One_success_is_evidence_but_not_enough_to_mint_a_rule()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var lessonId = await SeedLessonAsync(teamId);
        var runId = await SeedScoredExposureAsync(teamId, lessonId, headline: true);

        await QualifyAsync();

        var lesson = await ReadLessonAsync(lessonId);
        lesson.SuccessfulExposureRunIds.ShouldBe([runId]);
        lesson.QualifiedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Negative_exact_outcomes_are_durable_and_revoke_qualification_when_they_catch_up()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var lessonId = await SeedLessonAsync(teamId);
        await SeedScoredExposureAsync(teamId, lessonId, headline: true);
        await SeedScoredExposureAsync(teamId, lessonId, headline: true);
        await QualifyAsync();
        (await ReadLessonAsync(lessonId)).QualifiedAt.ShouldNotBeNull();

        await SeedScoredExposureAsync(teamId, lessonId, headline: false);
        await SeedScoredExposureAsync(teamId, lessonId, headline: false);
        await QualifyAsync();

        var lesson = await ReadLessonAsync(lessonId);
        lesson.NegativeExposureRunIds.Count.ShouldBe(2);
        lesson.QualifiedAt.ShouldBeNull("an equal negative body removes formal-rule status while retaining all audit evidence");
        lesson.QualificationSuppressedAt.ShouldNotBeNull("repeated negative evidence removes the lesson from prompt exploration without erasing it");
        using var scope = _fixture.BeginScope();
        var visible = await scope.Resolve<ILessonReader>().ListCurrentAsync(new LessonReadRequest(teamId, "supervisor", LessonRuntimeContext.General(), DateTimeOffset.UtcNow, 5), CancellationToken.None);
        visible.Select(row => row.Id).ShouldNotContain(lessonId);
    }

    [Fact]
    public async Task Qualification_runs_foreign_tenants_and_unexposed_scores_cannot_supply_evidence()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (foreignTeamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var lessonId = await SeedLessonAsync(teamId);
        await SeedScoredExposureAsync(teamId, lessonId, headline: true, purpose: "qualification");
        await SeedScoredExposureAsync(foreignTeamId, lessonId, headline: true);
        await SeedScoredExposureAsync(teamId, Guid.NewGuid(), headline: true);

        await QualifyAsync();

        var lesson = await ReadLessonAsync(lessonId);
        lesson.SuccessfulExposureRunIds.ShouldBeEmpty();
        lesson.NegativeExposureRunIds.ShouldBeEmpty();
        lesson.QualifiedAt.ShouldBeNull();
    }

    private async Task<int> QualifyAsync()
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<ILessonQualifier>().QualifyAsync(CancellationToken.None);
    }

    private async Task<Lesson> ReadLessonAsync(Guid lessonId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().Lesson.AsNoTracking().SingleAsync(lesson => lesson.Id == lessonId);
    }

    private async Task<Guid> SeedLessonAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var now = DateTimeOffset.UtcNow;
        var lesson = new Lesson
        {
            Id = Guid.NewGuid(), TeamId = teamId, Mode = "supervisor", FailureClass = "test", WhatFailed = "test", Why = "test", HowToApply = "test",
            SourceRunIds = [Guid.NewGuid()], DistilledByModel = "test-model", ValidFrom = now.AddMinutes(-5), ExpiresAt = now.AddDays(30),
        };
        scope.Resolve<CodeSpaceDbContext>().Lesson.Add(lesson);
        await scope.Resolve<CodeSpaceDbContext>().SaveChangesAsync();
        return lesson.Id;
    }

    private async Task<Guid> SeedScoredExposureAsync(Guid teamId, Guid lessonId, bool headline, string? purpose = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, SourceType = WorkflowRunSourceTypes.Manual, ActorType = "user", ActorId = SystemUsers.SeederId,
            NormalizedPayloadJson = "{}", Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = now, VerifiedAt = now, NormalizedAt = now,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId, TeamId = teamId, RunRequestId = requestId, SourceType = WorkflowRunSourceTypes.Manual, Status = headline ? WorkflowRunStatus.Success : WorkflowRunStatus.Failure,
            Purpose = purpose, CompletedAt = now, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        db.RunScorecard.Add(new RunScorecard
        {
            Id = Guid.NewGuid(), TeamId = teamId, WorkflowRunId = runId, CompletedAt = now, Solved = headline, Delivered = headline, HumanTouches = 0,
            UnattendedSolvedWithDelivery = headline, LessonArm = LessonArms.Injected, ScorerVersion = UnattendedDeliveryScorer.ScorerVersion,
        });
        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId, DecisionKind = "plan", IdempotencyKey = $"plan-{Guid.NewGuid():N}", InputHash = "test",
            Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}", FenceEpoch = 1, LessonArm = LessonArms.Injected, LessonIds = [lessonId],
        });
        await db.SaveChangesAsync();
        return runId;
    }
}
