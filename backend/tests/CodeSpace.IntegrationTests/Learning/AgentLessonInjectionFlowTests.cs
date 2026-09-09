using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Completion;
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

        await SeedAgentRunAsync(teamId, workflowRunId, first);

        using (var receiptScope = _fixture.BeginScope())
        {
            var receiptDb = receiptScope.Resolve<CodeSpaceDbContext>();
            var arms = await CodeSpace.Core.Services.Agents.Eval.RunLessonArms.ReadAsync(receiptDb, [workflowRunId], teamId, CancellationToken.None);
            var exposures = await CodeSpace.Core.Services.Agents.Eval.RunLessonExposures.ReadAsync(receiptDb, [workflowRunId], teamId, CancellationToken.None);
            arms[workflowRunId].ShouldBe(LessonArms.Injected);
            exposures[workflowRunId].ShouldBe([lesson.Id]);
        }

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
        task.LessonIds.ShouldBeEmpty("an upstream foreign id remains auditable on its assignment but cannot become a prompt exposure receipt");
        task.SystemPrompt.ShouldBeNull("a lesson owned by another tenant can never be rendered");
    }

    [Fact]
    public async Task Real_agent_run_creation_persists_the_shared_runtime_prompt_and_receipt()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var identityScope = _fixture.BeginScopeAs(userId, teamId);
        using var scope = identityScope.BeginLifetimeScope(builder => builder.RegisterInstance(AllRelevantEvaluator.Instance).As<ILessonRelevanceEvaluator>());
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
    public async Task Real_map_agent_creation_keys_receipts_by_cell_and_runtime()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var identityScope = _fixture.BeginScopeAs(userId, teamId);
        using var scope = identityScope.BeginLifetimeScope(builder => builder.RegisterInstance(AllRelevantEvaluator.Instance).As<ILessonRelevanceEvaluator>());
        var workflowRunId = await scope.Resolve<IRunFromSnapshotStarter>().StartFromSnapshotAsync(WorkflowsTestSeed.MinimalDefinition(), teamId, userId, "{}", [], TaskProjectionKinds.PlanMapSynth, null, CancellationToken.None);
        var lessonA = await SeedLessonAsync(teamId, RunModeKeys.PlanMap, "map model-a", new LessonRuntimeSeed { Models = ["model-a"] });
        var lessonB = await SeedLessonAsync(teamId, RunModeKeys.PlanMap, "map model-b", new LessonRuntimeSeed { Models = ["model-b"] });
        var goal = TaskTextFor(teamId, LessonArms.Injected);

        var runA = await scope.Resolve<IAgentRunService>().CreateAsync(new AgentTask { Goal = goal, Harness = "claude-code", Model = "model-a" }, teamId, workflowRunId, "map", "cell-a", CancellationToken.None);
        var runB = await scope.Resolve<IAgentRunService>().CreateAsync(new AgentTask { Goal = goal, Harness = "claude-code", Model = "model-b" }, teamId, workflowRunId, "map", "cell-b", CancellationToken.None);
        var taskA = JsonSerializer.Deserialize<AgentTask>(runA.TaskJson, AgentJson.Options)!;
        var taskB = JsonSerializer.Deserialize<AgentTask>(runB.TaskJson, AgentJson.Options)!;

        taskA.LessonArm.ShouldBe(LessonArms.Injected);
        taskB.LessonArm.ShouldBe(taskA.LessonArm);
        taskA.LessonIds.ShouldBe([lessonA.Id]);
        taskB.LessonIds.ShouldBe([lessonB.Id]);
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
        unknown.LessonArm.ShouldBe(LessonArms.Injected, "the run-wide arm is assigned because its scope has a lesson, while this prompt gets no inapplicable ids");
        unknown.LessonIds.ShouldBeEmpty();
        unknown.SystemPrompt.ShouldBeNull("missing model/tool facts cannot authorize a runtime-specific lesson");
    }

    [Fact]
    public async Task Heterogeneous_fanout_shares_the_arm_but_freezes_applicable_lessons_per_agent_runtime()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowRunId = await SeedRunAsync(teamId, TaskProjectionKinds.Supervisor, TaskTextFor(teamId, LessonArms.Injected));
        var lessonA = await SeedLessonAsync(teamId, "supervisor", "model-a lesson", new LessonRuntimeSeed { Models = ["model-a"] });
        var lessonB = await SeedLessonAsync(teamId, "supervisor", "model-b lesson", new LessonRuntimeSeed { Models = ["model-b"] });

        var agentA = await InjectAsync(new AgentTask { Goal = "unit a", SubtaskId = "unit-a", Harness = "claude-code", Model = "model-a" }, teamId, workflowRunId);
        var agentB = await InjectAsync(new AgentTask { Goal = "unit b", SubtaskId = "unit-b", Harness = "claude-code", Model = "model-b" }, teamId, workflowRunId);

        agentA.LessonArm.ShouldBe(LessonArms.Injected);
        agentB.LessonArm.ShouldBe(agentA.LessonArm, "one workflow is one treatment assignment");
        agentA.LessonIds.ShouldBe([lessonA.Id]);
        agentB.LessonIds.ShouldBe([lessonB.Id], "a first agent's model-specific receipt cannot leak into a heterogeneous sibling");
        agentA.SystemPrompt.ShouldContain("model-a lesson");
        agentA.SystemPrompt.ShouldNotContain("model-b lesson");
        agentB.SystemPrompt.ShouldContain("model-b lesson");
        agentB.SystemPrompt.ShouldNotContain("model-a lesson");
    }

    [Fact]
    public async Task A_first_runtime_with_no_match_does_not_hide_a_later_siblings_applicable_lesson()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowRunId = await SeedRunAsync(teamId, TaskProjectionKinds.Supervisor, TaskTextFor(teamId, LessonArms.Injected));
        var lessonB = await SeedLessonAsync(teamId, "supervisor", "model-b lesson", new LessonRuntimeSeed { Models = ["model-b"] });

        var agentA = await InjectAsync(new AgentTask { Goal = "unit a", SubtaskId = "unit-a", Harness = "claude-code", Model = "model-a" }, teamId, workflowRunId);
        var agentB = await InjectAsync(new AgentTask { Goal = "unit b", SubtaskId = "unit-b", Harness = "claude-code", Model = "model-b" }, teamId, workflowRunId);

        agentA.LessonArm.ShouldBe(LessonArms.Injected, "the run has a structurally eligible lesson even though this runtime does not match it");
        agentA.LessonIds.ShouldBeEmpty();
        agentB.LessonArm.ShouldBe(agentA.LessonArm);
        agentB.LessonIds.ShouldBe([lessonB.Id]);
    }

    [Fact]
    public async Task Legacy_run_assignment_ids_are_revalidated_for_each_new_runtime()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowRunId = await SeedRunAsync(teamId, TaskProjectionKinds.Supervisor);
        var lessonA = await SeedLessonAsync(teamId, "supervisor", "model-a lesson", new LessonRuntimeSeed { Models = ["model-a"] });
        var lessonB = await SeedLessonAsync(teamId, "supervisor", "model-b lesson", new LessonRuntimeSeed { Models = ["model-b"] });
        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IRunLessonAssignmentStore>().GetOrCreateAsync(new(workflowRunId, teamId, LessonArms.Injected, [lessonA.Id]), CancellationToken.None);

        var agentB = await InjectAsync(new AgentTask { Goal = "unit b", SubtaskId = "unit-b", Harness = "claude-code", Model = "model-b" }, teamId, workflowRunId);

        agentB.LessonIds.ShouldBe([lessonB.Id]);
        agentB.SystemPrompt.ShouldNotContain("model-a lesson", customMessage: "a pre-0215 first-agent receipt cannot leak across a heterogeneous rollout sibling");
    }

    [Fact]
    public async Task Concurrent_prompt_receipt_proposals_converge_on_one_immutable_tenant_bound_winner()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (foreignTeamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowRunId = await SeedRunAsync(teamId, TaskProjectionKinds.SingleAgent);
        var promptKey = new string('a', 64);
        var lessonA = Guid.NewGuid();
        var lessonB = Guid.NewGuid();

        var receipts = await Task.WhenAll(Enumerable.Range(0, 12).Select(async index =>
        {
            using var scope = _fixture.BeginScope();
            return await scope.Resolve<IAgentLessonPromptReceiptStore>().GetOrCreateAsync(new(workflowRunId, teamId, promptKey, [index % 2 == 0 ? lessonA : lessonB]), CancellationToken.None);
        }));

        receipts.Select(receipt => string.Join(',', receipt.LessonIds)).Distinct().Count().ShouldBe(1);
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        (await db.Database.SqlQuery<int>($"SELECT COUNT(*)::int AS \"Value\" FROM agent_lesson_prompt_receipt WHERE workflow_run_id = {workflowRunId}").SingleAsync()).ShouldBe(1);
        await Should.ThrowAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_lesson_prompt_receipt SET lesson_ids = ARRAY[]::uuid[] WHERE workflow_run_id = {workflowRunId}"));
        var capped = await verify.Resolve<IAgentLessonPromptReceiptStore>().GetOrCreateAsync(new(workflowRunId, teamId, new string('c', 64), Enumerable.Range(0, 12).Select(_ => Guid.NewGuid()).ToList()), CancellationToken.None);
        capped.LessonIds.Count.ShouldBe(AgentLessonPromptReceiptStore.MaxLessons);
        await Should.ThrowAsync<InvalidOperationException>(() => verify.Resolve<IAgentLessonPromptReceiptStore>().GetOrCreateAsync(new(workflowRunId, foreignTeamId, new string('b', 64), []), CancellationToken.None));
    }

    [Fact]
    public async Task Semantic_selection_is_frozen_with_candidates_and_excludes_an_unselected_structural_match()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowRunId = await SeedRunAsync(teamId, TaskProjectionKinds.SingleAgent, TaskTextFor(teamId, LessonArms.Injected));
        var selected = await SeedLessonAsync(teamId, "single-agent", "restore dependency failure", qualified: true);
        var unrelated = await SeedLessonAsync(teamId, "single-agent", "unrelated stylesheet failure", qualified: true);
        var evaluator = new FixedSelectionEvaluator([selected.Id]);
        var source = new AgentTask { Goal = "restore and compile the service", Harness = "claude-code" };

        var task = await InjectAsync(source, teamId, workflowRunId, evaluator);

        task.LessonIds.ShouldBe([selected.Id]);
        task.SystemPrompt.ShouldContain(LessonArms.Line(selected));
        task.SystemPrompt.ShouldNotContain(LessonArms.Line(unrelated));
        using var verify = _fixture.BeginScope();
        var key = AgentLessonInjector.PromptKey(new(source, teamId, workflowRunId, null, ""));
        var receipt = await verify.Resolve<IAgentLessonPromptReceiptStore>().ReadAsync(workflowRunId, teamId, key, CancellationToken.None);
        receipt.ShouldNotBeNull();
        receipt!.CandidateIds.ShouldBe([unrelated.Id, selected.Id], ignoreOrder: true);
        receipt.RelevanceStatus.ShouldBe(LessonRelevanceStatuses.Selected);
        receipt.RelevanceModel.ShouldBe("test-observed-model");
        receipt.RelevanceGeneration.ShouldBe(LlmLessonRelevanceEvaluator.Generation);
        receipt.AssessmentDigest.ShouldBe(new string('d', 64));
    }

    [Fact]
    public async Task Explicit_semantic_abstention_is_an_immutable_retry_receipt()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowRunId = await SeedRunAsync(teamId, TaskProjectionKinds.SingleAgent, TaskTextFor(teamId, LessonArms.Injected));
        var lesson = await SeedLessonAsync(teamId, "single-agent", "restore dependency failure", qualified: true);
        var abstaining = new FixedSelectionEvaluator([]);
        var source = new AgentTask { Goal = "compile the service", Harness = "claude-code" };

        var first = await InjectAsync(source, teamId, workflowRunId, abstaining);
        var retrySelector = new FixedSelectionEvaluator([lesson.Id]);
        var retry = await InjectAsync(source with { Goal = source.Goal + "\nretry feedback" }, teamId, workflowRunId, retrySelector);

        first.LessonIds.ShouldBeEmpty();
        first.SystemPrompt.ShouldBeNull();
        retry.LessonIds.ShouldBeEmpty("a retry reads the first abstention receipt instead of re-sampling model relevance");
        abstaining.Calls.ShouldBe(1);
        retrySelector.Calls.ShouldBe(0);
    }

    private async Task<AgentTask> InjectAsync(AgentTask task, Guid teamId, Guid workflowRunId, ILessonRelevanceEvaluator? evaluator = null)
    {
        using var scope = _fixture.BeginScope(builder => builder.RegisterInstance(evaluator ?? AllRelevantEvaluator.Instance).As<ILessonRelevanceEvaluator>());
        return await scope.Resolve<IAgentLessonInjector>().InjectAsync(new(task, teamId, workflowRunId, null, ""), CancellationToken.None);
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

    private async Task<Lesson> SeedLessonAsync(Guid teamId, string mode, string whatFailed = "restore failed", LessonRuntimeSeed? runtime = null, bool qualified = false)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var lesson = new Lesson
        {
            Id = Guid.NewGuid(), TeamId = teamId, Mode = mode, FailureClass = "build", WhatFailed = whatFailed, Why = "missing deps", HowToApply = "run restore first",
            ApplicableModels = runtime?.Models.ToList() ?? [], ApplicableHarnesses = runtime?.Harnesses.ToList() ?? [], RequiredTools = runtime?.Tools.ToList() ?? [],
            SourceRunIds = [Guid.NewGuid()], DistilledByModel = "test", ValidFrom = now.AddMinutes(-1), ExpiresAt = now.AddDays(1),
            SuccessfulExposureRunIds = qualified ? [Guid.NewGuid(), Guid.NewGuid()] : [], QualifiedAt = qualified ? now : null,
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

    private sealed class AllRelevantEvaluator : ILessonRelevanceEvaluator
    {
        public static readonly AllRelevantEvaluator Instance = new();
        public Task<LessonRelevanceResult> EvaluateAsync(LessonRelevanceRequest request, CancellationToken cancellationToken)
        {
            var selected = request.Candidates.Take(request.Take).ToList();
            return Task.FromResult(new LessonRelevanceResult(selected, request.Candidates.Select(lesson => lesson.Id).ToList(), selected.Count == 0 ? LessonRelevanceStatuses.NoCandidates : LessonRelevanceStatuses.Selected, "test-observed-model", selected.Count == 0 ? null : new string('d', 64)));
        }
    }

    private sealed class FixedSelectionEvaluator(IReadOnlyCollection<Guid> selectedIds) : ILessonRelevanceEvaluator
    {
        public int Calls { get; private set; }

        public Task<LessonRelevanceResult> EvaluateAsync(LessonRelevanceRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            var selected = request.Candidates.Where(lesson => selectedIds.Contains(lesson.Id)).Take(request.Take).ToList();
            return Task.FromResult(new LessonRelevanceResult(selected, request.Candidates.Select(lesson => lesson.Id).ToList(), selected.Count == 0 ? LessonRelevanceStatuses.Abstained : LessonRelevanceStatuses.Selected, "test-observed-model", new string('d', 64)));
        }
    }
}
