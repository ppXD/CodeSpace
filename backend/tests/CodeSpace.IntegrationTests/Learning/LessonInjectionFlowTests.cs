using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Completion;
using CodeSpace.Core.Services.Learning;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Planning.Planners;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows.Planning;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using System.Text.Json;

namespace CodeSpace.IntegrationTests.Learning;

/// <summary>
/// 🟢 Integration (real Postgres + real reader + real planner; fake structured client at the LLM seam): D2's
/// injection end to end — an injected-arm plan carries the lessons in its PROMPT and their ids + arm on the
/// PLAN (the A/B provenance the north-star referee slices); a withheld-arm plan of the same team sees no lesson
/// text yet records its arm; a lesson-less team plans outside the experiment ("none").
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class LessonInjectionFlowTests
{
    private readonly PostgresFixture _fixture;

    public LessonInjectionFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task An_injected_arm_plan_carries_the_lessons_and_their_provenance()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "claude-opus-4-8");
        var lessonId = await SeedLessonAsync(teamId, "run restore before check.sh");

        var taskText = TaskTextFor(teamId, LessonArms.Injected);
        var (plan, client) = await PlanAsync(teamId, taskText);

        plan.LessonArm.ShouldBe(LessonArms.Injected);
        plan.InjectedLessonIds.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBe(lessonId, "the plan records exactly which lessons it saw — the referee's provenance");
        client.LastUserPrompt.ShouldNotBeNull().ShouldContain("run restore before check.sh", customMessage: "the lesson must actually reach the brain, not just the ledger");
    }

    [Fact]
    public async Task A_withheld_arm_plan_records_its_arm_but_sees_no_lesson_text()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "claude-opus-4-8");
        await SeedLessonAsync(teamId, "run restore before check.sh");

        var taskText = TaskTextFor(teamId, LessonArms.Withheld);
        var (plan, client) = await PlanAsync(teamId, taskText);

        plan.LessonArm.ShouldBe(LessonArms.Withheld, "lessons existed and were deterministically held back — the control arm");
        plan.InjectedLessonIds.ShouldBeNull();
        client.LastUserPrompt.ShouldNotBeNull().ShouldNotContain("run restore before check.sh");
    }

    [Fact]
    public async Task A_lesson_less_team_plans_outside_the_experiment()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "claude-opus-4-8");

        var (plan, _) = await PlanAsync(teamId, "any task at all");

        plan.LessonArm.ShouldBe(LessonArms.None, "no lesson existed — this run must never be counted as a control");
        plan.InjectedLessonIds.ShouldBeNull();
    }

    [Fact]
    public async Task Planner_fails_closed_on_runtime_specific_lessons_until_a_subtask_runtime_is_resolved()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "claude-opus-4-8");
        var general = await SeedLessonAsync(teamId, "general-runtime");
        await SeedLessonAsync(teamId, "agent-model-specific", ["CLAUDE-OPUS-4-8"]);

        var (plan, client) = await PlanAsync(teamId, TaskTextFor(teamId, LessonArms.Injected));

        plan.InjectedLessonIds.ShouldBe([general]);
        client.LastUserPrompt.ShouldContain("general-runtime");
        client.LastUserPrompt.ShouldNotContain("agent-model-specific", customMessage: "the planner brain is not evidence of the future agent runtime");
    }

    [Fact]
    public async Task Semantic_selection_is_frozen_before_the_planner_call_and_reused_by_an_identical_retry()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "claude-opus-4-8");
        var selectedId = await SeedLessonAsync(teamId, "selected planner lesson", qualified: true);
        var unrelatedId = await SeedLessonAsync(teamId, "unrelated planner lesson", qualified: true);
        var runId = await SeedRunAsync(teamId);
        var taskText = TaskTextFor(teamId, LessonArms.Injected);
        var evaluator = new MutableEvaluator(selectedId);
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var memory = new PlannerLessonMemory(db, new LessonReader(db, evaluator), scope.Resolve<Microsoft.Extensions.Logging.ILogger<PlannerLessonMemory>>());
        var client = new CapturingClient();
        var planner = new LlmWorkflowPlanner(new FakeClients(client), scope.Resolve<IModelPoolSelector>(), scope.Resolve<IAgentHarnessRegistry>(), memory);
        var request = new WorkflowPlanRequest { TaskText = taskText, TaskGoal = taskText, TeamId = teamId, WorkflowRunId = runId, NodeId = "plan" };

        var first = await planner.PlanAsync(request, CancellationToken.None);
        evaluator.SelectedId = unrelatedId;
        var second = await planner.PlanAsync(request, CancellationToken.None);

        first.InjectedLessonIds.ShouldBe([selectedId]);
        second.InjectedLessonIds.ShouldBe([selectedId]);
        client.UserPrompts[^2].ShouldContain("selected planner lesson");
        client.UserPrompts[^2].ShouldNotContain("unrelated planner lesson");
        client.UserPrompts[^1].ShouldContain("selected planner lesson");
        client.UserPrompts[^1].ShouldNotContain("unrelated planner lesson");
        evaluator.Calls.ShouldBe(1, "the immutable pre-call receipt prevents an at-least-once retry from rerolling relevance");
    }

    [Fact]
    public async Task Concurrent_semantic_proposals_converge_on_one_tenant_bound_prompt_receipt()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var firstId = await SeedLessonAsync(teamId, "first candidate", qualified: true);
        var secondId = await SeedLessonAsync(teamId, "second candidate", qualified: true);
        var runId = await SeedRunAsync(teamId);
        var taskText = TaskTextFor(teamId, LessonArms.Injected);
        var request = new WorkflowPlanRequest { TaskText = taskText, TaskGoal = taskText, TeamId = teamId, WorkflowRunId = runId, NodeId = "plan" };

        var proposals = await Task.WhenAll(Enumerable.Range(0, 12).Select(async index =>
        {
            using var scope = _fixture.BeginScope();
            var db = scope.Resolve<CodeSpaceDbContext>();
            var selectedId = index % 2 == 0 ? firstId : secondId;
            var memory = new PlannerLessonMemory(db, new LessonReader(db, new MutableEvaluator(selectedId)), scope.Resolve<Microsoft.Extensions.Logging.ILogger<PlannerLessonMemory>>());
            return await memory.ResolveAsync(request, CancellationToken.None);
        }));

        proposals.Select(result => string.Join(',', result.LessonIds)).Distinct().Count().ShouldBe(1, "all callers must materialize the database winner, never their losing proposal");
        proposals[0].LessonIds.ShouldHaveSingleItem().ShouldBeOneOf(firstId, secondId);
        using var read = _fixture.BeginScope();
        var rows = await read.Resolve<CodeSpaceDbContext>().Database.SqlQuery<ReceiptCount>($"SELECT count(*)::int AS count FROM planner_lesson_prompt_receipt WHERE workflow_run_id = {runId} AND team_id = {teamId}").SingleAsync();
        rows.Count.ShouldBe(1);
    }

    // ─── Plumbing ────────────────────────────────────────────────────────────────

    /// <summary>The arm is a pure hash of (team, task text) — walk task texts until one lands on the wanted arm (deterministic, so the test stays stable).</summary>
    private static string TaskTextFor(Guid teamId, string arm)
    {
        for (var i = 0; i < 256; i++)
            if (LessonArms.Assign(teamId, $"fix the flaky test {i}") == arm) return $"fix the flaky test {i}";

        throw new InvalidOperationException("256 candidates never hit the arm — the hash is broken");
    }

    private async Task<(PlannedWorkflow Plan, CapturingClient Client)> PlanAsync(Guid teamId, string taskText)
    {
        using var scope = _fixture.BeginScope();
        var client = new CapturingClient();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var memory = new PlannerLessonMemory(db, new LessonReader(db, AllRelevantEvaluator.Instance), scope.Resolve<Microsoft.Extensions.Logging.ILogger<PlannerLessonMemory>>());
        var planner = new LlmWorkflowPlanner(new FakeClients(client), scope.Resolve<IModelPoolSelector>(), scope.Resolve<IAgentHarnessRegistry>(), memory);

        var plan = await planner.PlanAsync(new Messages.Dtos.Workflows.Planning.WorkflowPlanRequest { TaskText = taskText, TeamId = teamId }, CancellationToken.None);
        return (plan, client);
    }

    private async Task<Guid> SeedLessonAsync(Guid teamId, string howToApply, IReadOnlyList<string>? applicableModels = null, bool qualified = false)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var lesson = new Lesson
        {
            Id = Guid.NewGuid(), TeamId = teamId, Mode = RunModeKeys.PlanMap, FailureClass = "broken-acceptance-command",
            WhatFailed = "check.sh exits 2 on a clean tree", Why = "unrestored solution", HowToApply = howToApply,
            ApplicableModels = applicableModels?.Select(value => value.Trim().ToLowerInvariant()).ToList() ?? [],
            SourceRunIds = [Guid.NewGuid()], DistilledByModel = "test-model", ValidFrom = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.Add(LessonConsolidation.Lifetime),
            SuccessfulExposureRunIds = qualified ? [Guid.NewGuid(), Guid.NewGuid()] : [], QualifiedAt = qualified ? DateTimeOffset.UtcNow : null,
        };
        db.Lesson.Add(lesson);
        await db.SaveChangesAsync();
        return lesson.Id;
    }

    private async Task<Guid> SeedRunAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        db.WorkflowRunRequest.Add(new WorkflowRunRequest { Id = requestId, TeamId = teamId, SourceType = WorkflowRunSourceTypes.Manual, ActorType = "user", ActorId = SystemUsers.SeederId, NormalizedPayloadJson = "{}", Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = now, VerifiedAt = now, NormalizedAt = now });
        db.WorkflowRun.Add(new WorkflowRun { Id = runId, TeamId = teamId, RunRequestId = requestId, SourceType = WorkflowRunSourceTypes.Manual, ProjectionKind = TaskProjectionKinds.PlanMapDynamic, Status = WorkflowRunStatus.Running, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        await db.SaveChangesAsync();
        return runId;
    }

    private sealed class AllRelevantEvaluator : ILessonRelevanceEvaluator
    {
        public static readonly AllRelevantEvaluator Instance = new();
        public Task<LessonRelevanceResult> EvaluateAsync(LessonRelevanceRequest request, CancellationToken cancellationToken)
        {
            var selected = request.Candidates.Take(request.Take).ToList();
            return Task.FromResult(new LessonRelevanceResult(selected, request.Candidates.Select(lesson => lesson.Id).ToList(), LessonRelevanceStatuses.Selected, "test-model", new string('a', 64)));
        }
    }

    private sealed class MutableEvaluator(Guid selectedId) : ILessonRelevanceEvaluator
    {
        public Guid SelectedId { get; set; } = selectedId;
        public int Calls { get; private set; }
        public Task<LessonRelevanceResult> EvaluateAsync(LessonRelevanceRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            var selected = request.Candidates.Where(lesson => lesson.Id == SelectedId).ToList();
            return Task.FromResult(new LessonRelevanceResult(selected, request.Candidates.Select(lesson => lesson.Id).ToList(), LessonRelevanceStatuses.Selected, "test-model", new string('b', 64)));
        }
    }

    private sealed class FakeClients : ILLMClientRegistry
    {
        public FakeClients(IStructuredLLMClient structured) => All = new ILLMClient[] { (ILLMClient)structured };
        public IReadOnlyList<ILLMClient> All { get; }
        public ILLMClient Resolve(string provider) => All.First();
    }

    private sealed class CapturingClient : ILLMClient, IStructuredLLMClient
    {
        public string Provider => "Anthropic";
        public string? LastUserPrompt { get; private set; }
        public List<string> UserPrompts { get; } = [];
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken ct)
        {
            LastUserPrompt = request.UserPrompt;
            UserPrompts.Add(request.UserPrompt);
            return Task.FromResult(new StructuredLLMCompletion
            {
                Json = JsonSerializer.SerializeToElement(new { goal = "fix it", subtasks = new[] { new { id = "s1", title = "T", instruction = "do it" } } }),
                Model = request.Model,
            });
        }
    }

    private sealed record ReceiptCount(int Count);
}
