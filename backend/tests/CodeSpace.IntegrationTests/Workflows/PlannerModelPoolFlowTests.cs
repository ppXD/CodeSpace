using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Learning;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Planning.Planners;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Dtos.Workflows.Planning;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (real Postgres + real pool selector + real harness registry + the real planner; a capturing fake at
/// the structured-LLM seam): the capability catalog a plan-map planner allocates subtask models from is bounded to the
/// operator's allowed model pool. It used to list the WHOLE team pool, so a planner could only author a model the
/// dispatch then had to clamp; now it is shown the pool it must choose from, and an unbounded request is unchanged.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PlannerModelPoolFlowTests
{
    private readonly PostgresFixture _fixture;

    public PlannerModelPoolFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task The_planners_catalog_lists_only_the_allowed_pool_and_the_whole_pool_when_unbounded()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var pooled = await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "pooled-sonnet");
        await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "outside-opus");

        var bounded = await PlannerPromptAsync(new WorkflowPlanRequest { TaskText = "split the migration", TeamId = teamId, AllowedModelIds = [pooled.RowId] });
        var unbounded = await PlannerPromptAsync(new WorkflowPlanRequest { TaskText = "split the migration", TeamId = teamId });

        bounded.ShouldContain("pooled-sonnet", customMessage: "the pooled model is on the planner's menu");
        bounded.ShouldNotContain("outside-opus", customMessage: "a model outside the operator's pool must not be offered to the planner at all");
        unbounded.ShouldContain("pooled-sonnet");
        unbounded.ShouldContain("outside-opus", customMessage: "no pool ⇒ the whole team pool, the catalog the planner has always seen");
    }

    private async Task<string> PlannerPromptAsync(WorkflowPlanRequest request)
    {
        using var scope = _fixture.BeginScope();
        var client = new CapturingClient();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var memory = new PlannerLessonMemory(db, new LessonReader(db, NoLessons.Instance), scope.Resolve<Microsoft.Extensions.Logging.ILogger<PlannerLessonMemory>>());
        var planner = new LlmWorkflowPlanner(new SingleClient(client), scope.Resolve<IModelPoolSelector>(), scope.Resolve<IAgentHarnessRegistry>(), memory);

        await planner.PlanAsync(request, CancellationToken.None);
        return client.LastUserPrompt.ShouldNotBeNull("the planner never reached the model");
    }

    /// <summary>No lesson is seeded, so relevance is never asked; a call here is a planner reaching for lessons it cannot have.</summary>
    private sealed class NoLessons : ILessonRelevanceEvaluator
    {
        public static readonly NoLessons Instance = new();
        public Task<LessonRelevanceResult> EvaluateAsync(LessonRelevanceRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class SingleClient : ILLMClientRegistry
    {
        public SingleClient(IStructuredLLMClient structured) => All = new ILLMClient[] { (ILLMClient)structured };
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
                Json = JsonSerializer.SerializeToElement(new { goal = "split it", subtasks = new[] { new { id = "s1", title = "T", instruction = "do it" } } }),
                Model = request.Model,
            });
        }
    }
}
