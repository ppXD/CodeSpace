using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Learning;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using System.Text.Json;

namespace CodeSpace.IntegrationTests.Learning;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AgentLessonInjectionFlowTests
{
    private readonly PostgresFixture _fixture;

    public AgentLessonInjectionFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Quick_agent_gets_a_deterministic_direct_lesson_treatment_and_retry_reuses_it()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var lesson = await SeedLessonAsync(teamId, "single-agent");
        var operatorGoal = Enumerable.Range(0, 1000).Select(i => $"generic task {i}").First(value => LessonArms.Assign(teamId, value) == LessonArms.Injected);
        var decoratedGoal = Enumerable.Range(0, 1000).Select(i => $"# Earlier turns\n{i}\n{operatorGoal}").First(value => LessonArms.Assign(teamId, value) == LessonArms.Withheld);
        var workflowRunId = await SeedRunAsync(teamId, TaskProjectionKinds.SingleAgent, operatorGoal);

        var first = await InjectAsync(new AgentTask { Goal = decoratedGoal, DisplayTitle = "a display fallback that must not win", Harness = "claude-code" }, teamId, workflowRunId);
        first.LessonArm.ShouldBe(LessonArms.Injected);
        first.LessonIds.ShouldBe([lesson.Id]);
        first.SystemPrompt.ShouldNotBeNull();
        first.SystemPrompt!.ShouldContain(LessonArms.Line(lesson));
        first.Goal.ShouldBe(decoratedGoal, "prompt decoration stays intact while assignment hashes the launch's operator goal");

        using (var receiptScope = _fixture.BeginScope())
        {
            var receiptDb = receiptScope.Resolve<CodeSpaceDbContext>();
            var arms = await CodeSpace.Core.Services.Agents.Eval.RunLessonArms.ReadAsync(receiptDb, [workflowRunId], teamId, CancellationToken.None);
            var exposures = await CodeSpace.Core.Services.Agents.Eval.RunLessonExposures.ReadAsync(receiptDb, [workflowRunId], teamId, CancellationToken.None);
            arms[workflowRunId].ShouldBe(LessonArms.Injected);
            exposures[workflowRunId].ShouldBe([lesson.Id]);
        }

        await SeedAgentRunAsync(teamId, workflowRunId, first);

        await InvalidateAsync(lesson.Id);
        var retry = await InjectAsync(first with { Goal = decoratedGoal + "\nretry feedback" }, teamId, workflowRunId);
        retry.LessonArm.ShouldBe(first.LessonArm, "a retry cannot reroll its experiment assignment");
        retry.LessonIds.ShouldBe(first.LessonIds, "the exact treatment stays frozen even if the live reader changes");
    }

    [Fact]
    public async Task Concurrent_first_dispatches_atomically_share_one_immutable_assignment()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowRunId = await SeedRunAsync(teamId, TaskProjectionKinds.SingleAgent);
        var lessonId = Guid.NewGuid();

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(async index =>
        {
            using var scope = _fixture.BeginScope();
            var proposal = index % 2 == 0
                ? new RunLessonAssignmentProposal(workflowRunId, teamId, LessonArms.Injected, [lessonId])
                : new RunLessonAssignmentProposal(workflowRunId, teamId, LessonArms.Withheld, []);
            return await scope.Resolve<IRunLessonAssignmentStore>().GetOrCreateAsync(proposal, CancellationToken.None);
        }));

        results.Select(result => result.Arm).Distinct().Count().ShouldBe(1);
        results.Select(result => string.Join(',', result.LessonIds)).Distinct().Count().ShouldBe(1);

        using var verificationScope = _fixture.BeginScope();
        var rows = await verificationScope.Resolve<CodeSpaceDbContext>().Database.SqlQuery<int>($"SELECT COUNT(*)::int AS \"Value\" FROM workflow_run_lesson_assignment WHERE workflow_run_id = {workflowRunId}").SingleAsync();
        rows.ShouldBe(1);
    }

    [Fact]
    public async Task Assignment_is_tenant_bound_and_missing_foreign_history_never_crosses_the_prompt_boundary()
    {
        var (teamA, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (teamB, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowRunId = await SeedRunAsync(teamA, TaskProjectionKinds.SingleAgent);
        var foreignLesson = await SeedLessonAsync(teamB, "single-agent");

        using (var scope = _fixture.BeginScope())
        {
            var store = scope.Resolve<IRunLessonAssignmentStore>();
            await store.GetOrCreateAsync(new(workflowRunId, teamA, LessonArms.Injected, [foreignLesson.Id]), CancellationToken.None);
            (await store.ReadAsync(workflowRunId, teamB, CancellationToken.None)).ShouldBeNull();
            await Should.ThrowAsync<InvalidOperationException>(() => store.GetOrCreateAsync(new(workflowRunId, teamB, LessonArms.Withheld, []), CancellationToken.None));
        }

        var task = await InjectAsync(new AgentTask { Goal = "tenant-isolated", Harness = "claude-code" }, teamA, workflowRunId);
        task.LessonArm.ShouldBe(LessonArms.Injected);
        task.LessonIds.ShouldBe([foreignLesson.Id], "the immutable receipt remains honest about unavailable history");
        task.SystemPrompt.ShouldBeNull("a lesson owned by another tenant can never be rendered");
    }

    [Fact]
    public async Task Real_agent_run_creation_persists_the_shared_runtime_prompt_and_receipt()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var scope = _fixture.BeginScopeAs(userId, teamId);
        var workflowRunId = await scope.Resolve<IRunFromSnapshotStarter>().StartFromSnapshotAsync(WorkflowsTestSeed.MinimalDefinition(), teamId, userId, "{}", [], TaskProjectionKinds.SingleAgent, null, CancellationToken.None);
        var lesson = await SeedLessonAsync(teamId, "single-agent");
        var goal = Enumerable.Range(0, 1000).Select(i => $"runtime task {i}").First(value => LessonArms.Assign(teamId, value) == LessonArms.Injected);

        var run = await scope.Resolve<IAgentRunService>().CreateAsync(new AgentTask { Goal = goal, Harness = "claude-code" }, teamId, workflowRunId, "agent", cancellationToken: CancellationToken.None);
        var persisted = JsonSerializer.Deserialize<AgentTask>(run.TaskJson, AgentJson.Options)!;

        persisted.LessonArm.ShouldBe(LessonArms.Injected);
        persisted.LessonIds.ShouldBe([lesson.Id]);
        persisted.SystemPrompt.ShouldNotBeNull();
        persisted.SystemPrompt!.ShouldContain(LessonArms.Line(lesson));
        persisted.Goal.ShouldBe(goal);
    }

    [Fact]
    public async Task Agent_task_receipts_feed_the_shared_run_arm_and_exact_exposure_readers()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowRunId = await SeedRunAsync(teamId, TaskProjectionKinds.SingleAgent);
        var lesson = await SeedLessonAsync(teamId, "single-agent");
        var task = AgentLessonPrompt.Inject(new AgentTask { Goal = "task", Harness = "claude-code" }, LessonArms.Injected, [lesson]);
        await SeedAgentRunAsync(teamId, workflowRunId, task);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var arms = await CodeSpace.Core.Services.Agents.Eval.RunLessonArms.ReadAsync(db, [workflowRunId], teamId, CancellationToken.None);
        var exposures = await CodeSpace.Core.Services.Agents.Eval.RunLessonExposures.ReadAsync(db, [workflowRunId], teamId, CancellationToken.None);

        arms[workflowRunId].ShouldBe(LessonArms.Injected);
        exposures[workflowRunId].ShouldBe([lesson.Id]);
    }

    [Fact]
    public async Task Plan_map_worker_reuses_the_planners_exact_receipt_instead_of_rerolling_its_subtask()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowRunId = await SeedRunAsync(teamId, TaskProjectionKinds.PlanMapSynth);
        var lesson = await SeedLessonAsync(teamId, "plan-map");
        await SeedPlannerReceiptAsync(workflowRunId, lesson.Id);

        var task = await InjectAsync(new AgentTask { Goal = "a model-authored map subtask", Harness = "claude-code" }, teamId, workflowRunId);

        task.LessonArm.ShouldBe(LessonArms.Injected);
        task.LessonIds.ShouldBe([lesson.Id]);
        task.SystemPrompt.ShouldNotBeNull();
        task.SystemPrompt!.ShouldContain(LessonArms.Line(lesson));
    }

    [Fact]
    public async Task Supervisor_spawn_reuses_the_decision_tapes_exact_receipt()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowRunId = await SeedRunAsync(teamId, TaskProjectionKinds.Supervisor);
        var lesson = await SeedLessonAsync(teamId, "supervisor");
        await SeedSupervisorReceiptAsync(teamId, workflowRunId, [lesson.Id]);

        var task = await InjectAsync(new AgentTask { Goal = "a supervisor-authored subtask", Harness = "claude-code" }, teamId, workflowRunId);

        task.LessonArm.ShouldBe(LessonArms.Injected);
        task.LessonIds.ShouldBe([lesson.Id]);
        task.SystemPrompt.ShouldNotBeNull();
        task.SystemPrompt!.ShouldContain(LessonArms.Line(lesson));
    }

    [Fact]
    public async Task Historical_prompt_receipt_preserves_the_original_ranked_order()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowRunId = await SeedRunAsync(teamId, TaskProjectionKinds.Supervisor);
        var first = await SeedLessonAsync(teamId, "supervisor", "first failure");
        var second = await SeedLessonAsync(teamId, "supervisor", "second failure");
        await SeedSupervisorReceiptAsync(teamId, workflowRunId, [second.Id, first.Id]);

        var task = await InjectAsync(new AgentTask { Goal = "ordered history", Harness = "claude-code" }, teamId, workflowRunId);

        task.LessonIds.ShouldBe([second.Id, first.Id]);
        task.SystemPrompt!.IndexOf(LessonArms.Line(second), StringComparison.Ordinal).ShouldBeLessThan(task.SystemPrompt.IndexOf(LessonArms.Line(first), StringComparison.Ordinal));
    }

    [Fact]
    public async Task First_runtime_assignment_filters_on_the_resolved_model_harness_and_complete_tool_set()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var matchingRun = await SeedRunAsync(teamId, TaskProjectionKinds.SingleAgent, TaskTextFor(teamId, LessonArms.Injected));
        var unknownRun = await SeedRunAsync(teamId, TaskProjectionKinds.SingleAgent, TaskTextFor(teamId, LessonArms.Injected));
        var lesson = await SeedLessonAsync(teamId, "single-agent", runtime: new LessonRuntimeSeed
        {
            Models = ["claude-opus-4-8"], Harnesses = ["claude-code"], Tools = ["git.status", "git.diff"],
        });

        var matching = await InjectAsync(new AgentTask { Goal = "task", Harness = "CLAUDE-CODE", Model = "CLAUDE-OPUS-4-8", Tools = ["git.diff", "git.status", "extra"] }, teamId, matchingRun);
        var unknown = await InjectAsync(new AgentTask { Goal = "task", Harness = "claude-code" }, teamId, unknownRun);

        matching.LessonIds.ShouldBe([lesson.Id]);
        matching.SystemPrompt.ShouldContain(LessonArms.Line(lesson));
        unknown.LessonArm.ShouldBe(LessonArms.None);
        unknown.LessonIds.ShouldBeEmpty();
        unknown.SystemPrompt.ShouldBeNull("missing model/tool facts cannot authorize a runtime-specific lesson");
    }

    private async Task<AgentTask> InjectAsync(AgentTask task, Guid teamId, Guid workflowRunId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IAgentLessonInjector>().InjectAsync(task, teamId, workflowRunId, CancellationToken.None);
    }

    private static string TaskTextFor(Guid teamId, string arm)
    {
        for (var i = 0; i < 100_000; i++)
        {
            var goal = $"runtime-applicability-{i}";
            if (LessonArms.Assign(teamId, goal) == arm) return goal;
        }

        throw new InvalidOperationException($"Could not find deterministic {arm} assignment");
    }

    private async Task<Guid> SeedRunAsync(Guid teamId, string projectionKind, string? operatorGoal = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        db.WorkflowRunRequest.Add(new WorkflowRunRequest { Id = requestId, TeamId = teamId, SourceType = WorkflowRunSourceTypes.Manual, ActorType = "user", ActorId = SystemUsers.SeederId, NormalizedPayloadJson = operatorGoal is null ? "{}" : JsonSerializer.Serialize(new { goal = operatorGoal }), Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = now, VerifiedAt = now, NormalizedAt = now });
        db.WorkflowRun.Add(new WorkflowRun { Id = runId, TeamId = teamId, RunRequestId = requestId, SourceType = WorkflowRunSourceTypes.Manual, ProjectionKind = projectionKind, Status = WorkflowRunStatus.Running, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        await db.SaveChangesAsync();
        return runId;
    }

    private async Task<Lesson> SeedLessonAsync(Guid teamId, string mode, string whatFailed = "restore failed", LessonRuntimeSeed? runtime = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var lesson = new Lesson
        {
            Id = Guid.NewGuid(), TeamId = teamId, Mode = mode, FailureClass = "build", WhatFailed = whatFailed, Why = "missing deps", HowToApply = "run restore first",
            ApplicableModels = runtime?.Models.ToList() ?? [], ApplicableHarnesses = runtime?.Harnesses.ToList() ?? [], RequiredTools = runtime?.Tools.ToList() ?? [],
            SourceRunIds = [Guid.NewGuid()], DistilledByModel = "test", ValidFrom = now.AddMinutes(-1), ExpiresAt = now.AddDays(1),
        };
        db.Lesson.Add(lesson);
        await db.SaveChangesAsync();
        return lesson;
    }

    private async Task SeedAgentRunAsync(Guid teamId, Guid workflowRunId, AgentTask task)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.AgentRun.Add(new AgentRun { Id = Guid.NewGuid(), TeamId = teamId, WorkflowRunId = workflowRunId, NodeId = "agent", Harness = task.Harness, Status = AgentRunStatus.Queued, TaskJson = JsonSerializer.Serialize(task, AgentJson.Options) });
        await db.SaveChangesAsync();
    }

    private async Task SeedPlannerReceiptAsync(Guid workflowRunId, Guid lessonId)
    {
        using var scope = _fixture.BeginScope();
        scope.Resolve<CodeSpaceDbContext>().WorkflowRunRecord.Add(new WorkflowRunRecord
        {
            Id = Guid.NewGuid(), RunId = workflowRunId, NodeId = "plan", RecordType = WorkflowRunRecordTypes.NodeCompleted,
            PayloadJson = JsonSerializer.Serialize(new { outputs = new { lessonArm = LessonArms.Injected, injectedLessonIds = new[] { lessonId } } }),
        });
        await scope.Resolve<CodeSpaceDbContext>().SaveChangesAsync();
    }

    private async Task SeedSupervisorReceiptAsync(Guid teamId, Guid workflowRunId, IReadOnlyList<Guid> lessonIds)
    {
        using var scope = _fixture.BeginScope();
        scope.Resolve<CodeSpaceDbContext>().SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = workflowRunId, Sequence = 1, DecisionKind = SupervisorDecisionKinds.Plan,
            IdempotencyKey = Guid.NewGuid().ToString("N"), InputHash = "test", Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = "{}", LessonArm = LessonArms.Injected, LessonIds = lessonIds.ToList(), FenceEpoch = 1,
        });
        await scope.Resolve<CodeSpaceDbContext>().SaveChangesAsync();
    }

    private async Task InvalidateAsync(Guid lessonId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().Lesson.Where(lesson => lesson.Id == lessonId).ExecuteUpdateAsync(setters => setters.SetProperty(lesson => lesson.InvalidatedAt, DateTimeOffset.UtcNow));
    }

    private sealed record LessonRuntimeSeed
    {
        public IReadOnlyList<string> Models { get; init; } = [];
        public IReadOnlyList<string> Harnesses { get; init; } = [];
        public IReadOnlyList<string> Tools { get; init; } = [];
    }
}
