using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Review;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// The agent-run store's half of the secret-taint rule, against real Postgres: a task created while a workflow node
/// executes (a supervisor's spawn, a plan reviewer — every caller that is not the engine's own agent.run staging) is
/// sealed with that node scope's secrets, read from the ambient capture redactor the engine pushes for the node; and
/// a review child, briefed with its producer's goal, inherits the producer's sealed secrets. In both, task_jsonb is
/// masked, the sealed copy opens to the exact task, and the opened task still names the secrets for the executor.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AgentTaskSealingFlowTests
{
    private readonly PostgresFixture _fixture;

    public AgentTaskSealingFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_task_created_inside_a_node_scope_is_sealed_with_that_scopes_secrets()
    {
        var canary = Canary();
        var seed = await SeedAsync();
        using var scope = _fixture.BeginScope();
        var runId = await scope.Resolve<IRunStarter>().StartAsync(Manual(seed), CancellationToken.None);
        var runs = scope.Resolve<IAgentRunService>();

        AgentRun inNode;
        using (LlmCallContext.Push(NodeScope(scope, seed, runId, new PersistenceSecretRedactor([canary, "a-scope-secret-the-task-never-names"]))))
            inNode = await runs.CreateAsync(Task($"Deploy with {canary}"), seed.TeamId, runId, "sup", cancellationToken: CancellationToken.None);

        var stored = await LoadAsync(inNode.Id);
        stored.TaskJson.ShouldNotContain(canary);
        stored.SealedTaskCiphertext.ShouldNotBeNull();

        var opened = AgentTaskSecrets.Open(stored, scope.Resolve<WorkflowSensitivePayloadSealer>())!;
        opened.Goal.ShouldBe($"Deploy with {canary}", "the executor opens the exact goal");
        opened.WorkflowSecrets.ShouldBe([canary], "only the scope values the task embeds ride it");
    }

    [Fact]
    public async Task A_task_created_outside_any_node_scope_is_stored_as_given()
    {
        // No node scope, no secret this store could know of: a standalone launch persists byte-for-byte as before.
        var seed = await SeedAsync();
        using var scope = _fixture.BeginScope();
        var runId = await scope.Resolve<IRunStarter>().StartAsync(Manual(seed), CancellationToken.None);

        var created = await scope.Resolve<IAgentRunService>().CreateAsync(Task("Summarize the release"), seed.TeamId, runId, "agent", cancellationToken: CancellationToken.None);

        var stored = await LoadAsync(created.Id);
        stored.SealedTaskCiphertext.ShouldBeNull();
        JsonSerializer.Deserialize<AgentTask>(stored.TaskJson, AgentJson.Options)!.Goal.ShouldBe("Summarize the release");
    }

    [Fact]
    public async Task A_review_child_inherits_its_producers_sealed_secrets()
    {
        var canary = Canary();
        var seed = await SeedAsync();
        using var scope = _fixture.BeginScope();
        var repositoryId = await SeedRepositoryAsync(scope, seed.TeamId);
        var runId = await scope.Resolve<IRunStarter>().StartAsync(Manual(seed), CancellationToken.None);
        var runs = scope.Resolve<IAgentRunService>();

        var producer = await runs.CreateAsync(Task($"Deploy with {canary}") with { RepositoryId = repositoryId, WorkflowSecrets = [canary] }, seed.TeamId, runId, "producer", cancellationToken: CancellationToken.None);
        var owner = (await runs.ClaimOwnershipAsync(producer.Id, CancellationToken.None)).ShouldNotBeNull();
        var reviewTask = AgentReviewRunner.BuildReviewTask(new AgentReviewSpec { ParentOwner = owner, RepositoryId = repositoryId, TeamId = seed.TeamId, SubjectInstructions = $"Review the work for: Deploy with {canary}", IterationKey = "#review" }, "test");

        var child = await runs.CreateReviewAsync(new AgentReviewCreation(owner, seed.TeamId, reviewTask), CancellationToken.None);

        var stored = await LoadAsync(child.Id);
        stored.TaskJson.ShouldNotContain(canary, customMessage: "the reviewer is briefed with the producer's goal, so its task carries the producer's secret too");
        AgentTaskSecrets.Open(stored, scope.Resolve<WorkflowSensitivePayloadSealer>())!.WorkflowSecrets.ShouldBe([canary]);
    }

    private static string Canary() => $"CANARY-sealing-{Guid.NewGuid():N}";

    private static AgentTask Task(string goal) => new() { Goal = goal, Harness = "test", Autonomy = AgentAutonomyLevel.Unleashed, Permissions = AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Unleashed) };

    private static LlmCallScope NodeScope(ILifetimeScope scope, Seed seed, Guid runId, PersistenceSecretRedactor redactor) =>
        new(runId, seed.TeamId, "sup", "", "agent.supervisor", scope.Resolve<IRunRecordLogger>(), scope.Resolve<IArtifactOffloader>(), CaptureRedactor: redactor);

    private async Task<AgentRun> LoadAsync(Guid agentRunId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(run => run.Id == agentRunId);
    }

    private async Task<Seed> SeedAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var scope = _fixture.BeginScopeAs(userId, teamId);
        var workflowId = await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand { Name = $"sealing-{Guid.NewGuid():N}", Definition = WorkflowsTestSeed.MinimalDefinition(), Activations = [], Enabled = true });

        return new Seed(teamId, userId, workflowId);
    }

    private static async Task<Guid> SeedRepositoryAsync(ILifetimeScope scope, Guid teamId)
    {
        var db = scope.Resolve<CodeSpaceDbContext>();
        var provider = new ProviderInstance { Id = Guid.NewGuid(), TeamId = teamId, Provider = ProviderKind.GitHub, DisplayName = "review", BaseUrl = "https://example.test" };
        var repository = new Repository { Id = Guid.NewGuid(), TeamId = teamId, ProviderInstanceId = provider.Id, ExternalId = Guid.NewGuid().ToString(), NamespacePath = "team", Name = "repo", FullPath = "team/repo", WebUrl = "https://example.test/repo" };

        db.ProviderInstance.Add(provider);
        db.Repository.Add(repository);
        await db.SaveChangesAsync();

        return repository.Id;
    }

    private static RunSourceEnvelope Manual(Seed seed) => new() { TeamId = seed.TeamId, WorkflowId = seed.WorkflowId, WorkflowVersion = 1, SourceType = WorkflowRunSourceTypes.Manual, ActorType = WorkflowRunActorTypes.User, ActorId = seed.UserId, CreatedBy = seed.UserId, NormalizedPayloadJson = "{}" };

    private sealed record Seed(Guid TeamId, Guid UserId, Guid WorkflowId);
}
