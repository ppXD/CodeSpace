using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Learning;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Planning.Planners;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Dtos.Workflows.Planning;
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
        var planner = new LlmWorkflowPlanner(new FakeClients(client), scope.Resolve<IModelPoolSelector>(), scope.Resolve<IAgentHarnessRegistry>(), scope.Resolve<ILessonReader>());

        var plan = await planner.PlanAsync(new Messages.Dtos.Workflows.Planning.WorkflowPlanRequest { TaskText = taskText, TeamId = teamId }, CancellationToken.None);
        return (plan, client);
    }

    private async Task<Guid> SeedLessonAsync(Guid teamId, string howToApply)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var lesson = new Lesson
        {
            Id = Guid.NewGuid(), TeamId = teamId, Mode = "supervisor", FailureClass = "broken-acceptance-command",
            WhatFailed = "check.sh exits 2 on a clean tree", Why = "unrestored solution", HowToApply = howToApply,
            SourceRunIds = [Guid.NewGuid()], DistilledByModel = "test-model", ValidFrom = DateTimeOffset.UtcNow,
        };
        db.Lesson.Add(lesson);
        await db.SaveChangesAsync();
        return lesson.Id;
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
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken ct)
        {
            LastUserPrompt = request.UserPrompt;
            return Task.FromResult(new StructuredLLMCompletion
            {
                Json = JsonSerializer.SerializeToElement(new { title = "t", subtasks = new[] { new { id = "s1", title = "T", instruction = "do it" } } }),
                Model = request.Model,
            });
        }
    }
}
