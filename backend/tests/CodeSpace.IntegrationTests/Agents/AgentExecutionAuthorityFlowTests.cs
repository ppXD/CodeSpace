using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Authority.Exceptions;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Failures;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed partial class AgentExecutionAuthorityFlowTests
{
    private readonly PostgresFixture _fixture;

    public AgentExecutionAuthorityFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Authored_admission_rejects_a_launcher_whose_membership_was_revoked()
    {
        var seed = await SeedAsync();
        await RevokeAsync(seed);
        using var scope = _fixture.BeginScope();
        var exception = await Should.ThrowAsync<Exception>(() => scope.Resolve<IRunStarter>().StartAsync(Manual(seed), CancellationToken.None));
        AssertAuthorityFailure(exception);
        (await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.CountAsync(r => r.TeamId == seed.TeamId)).ShouldBe(0);
    }

    [Fact]
    public async Task Authored_admission_rejects_an_unknown_system_actor_without_a_delegation()
    {
        var seed = await SeedAsync();
        using var scope = _fixture.BeginScope();
        var exception = await Should.ThrowAsync<Exception>(() => scope.Resolve<IRunStarter>().StartAsync(Manual(seed) with { ActorType = WorkflowRunActorTypes.System, ActorId = SystemUsers.SeederId }, CancellationToken.None));
        AssertAuthorityFailure(exception);
    }

    [Fact]
    public async Task Snapshot_admission_does_not_turn_viewer_membership_into_a_launch_grant()
    {
        var seed = await SeedAsync();
        using var scope = _fixture.BeginScopeAs(seed.UserId, seed.TeamId);
        await scope.Resolve<CodeSpaceDbContext>().TeamMembership.Where(m => m.TeamId == seed.TeamId && m.UserId == seed.UserId).ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, TeamRole.Viewer));
        var exception = await Should.ThrowAsync<Exception>(() => scope.Resolve<IRunFromSnapshotStarter>().StartFromSnapshotAsync(WorkflowsTestSeed.MinimalDefinition(), seed.TeamId, seed.UserId, "{}", [], null, null, CancellationToken.None));
        AssertAuthorityFailure(exception);
    }

    [Fact]
    public async Task Standalone_background_seeder_is_not_an_operator_grant()
    {
        var seed = await SeedAsync();
        using var scope = _fixture.BeginScope();
        var exception = await Should.ThrowAsync<AgentAuthorityDeniedException>(() => scope.Resolve<IAgentRunService>().CreateAsync(Task(), seed.TeamId, null, null, cancellationToken: CancellationToken.None));
        AssertAuthorityFailure(exception);
        exception.Reason.ShouldBe("actor-unverifiable", "a standalone admission resolves its launcher from ICurrentUser, so a scope that names no operator is refused for THAT reason");
        (await scope.Resolve<CodeSpaceDbContext>().AgentRun.CountAsync(r => r.TeamId == seed.TeamId)).ShouldBe(0);

        // The other half of the fixture shape every standalone-run test now depends on: the SAME call under the
        // seeded owner is admitted, and its receipt names that owner as the launcher.
        using var launcher = _fixture.BeginScopeAs(seed.UserId, seed.TeamId);
        var admitted = await launcher.Resolve<IAgentRunService>().CreateAsync(Task(), seed.TeamId, null, null, cancellationToken: CancellationToken.None);
        admitted.Status.ShouldBe(AgentRunStatus.Queued);
        var receipt = JsonSerializer.Deserialize<AgentTask>(admitted.TaskJson, AgentJson.Options)!.ExecutionAuthority.ShouldNotBeNull();
        receipt.SourceKind.ShouldBe("standalone");
        receipt.Subjects.ShouldHaveSingleItem().UserId.ShouldBe(seed.UserId);
    }

    [Fact]
    public async Task Queued_agent_cannot_start_after_launcher_membership_is_revoked()
    {
        var seed = await SeedAsync();
        Guid agentId;
        using (var scope = _fixture.BeginScopeAs(seed.UserId, seed.TeamId))
        {
            var runId = await scope.Resolve<IRunStarter>().StartAsync(Manual(seed), CancellationToken.None);
            agentId = (await scope.Resolve<IAgentRunService>().CreateAsync(Task(), seed.TeamId, runId, "agent", cancellationToken: CancellationToken.None)).Id;
        }
        await RevokeAsync(seed);
        using var claim = _fixture.BeginScope();
        var exception = await Should.ThrowAsync<Exception>(() => claim.Resolve<IAgentRunService>().MarkRunningAsync(agentId, CancellationToken.None));
        AssertAuthorityFailure(exception);
        (await claim.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.Id == agentId)).Status.ShouldBe(AgentRunStatus.Queued);
    }

    [Fact]
    public async Task Valid_member_keeps_existing_explicit_unleashed_intent()
    {
        var seed = await SeedAsync();
        using var scope = _fixture.BeginScopeAs(seed.UserId, seed.TeamId);
        await scope.Resolve<CodeSpaceDbContext>().TeamMembership.Where(m => m.TeamId == seed.TeamId && m.UserId == seed.UserId).ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, TeamRole.Member));
        var runId = await scope.Resolve<IRunStarter>().StartAsync(Manual(seed), CancellationToken.None);
        var agent = await scope.Resolve<IAgentRunService>().CreateAsync(Task(), seed.TeamId, runId, "agent", cancellationToken: CancellationToken.None);
        JsonSerializer.Deserialize<AgentTask>(agent.TaskJson, AgentJson.Options)!.Autonomy.ShouldBe(AgentAutonomyLevel.Unleashed);
        (await scope.Resolve<IAgentRunService>().MarkRunningAsync(agent.Id, CancellationToken.None)).ShouldBe(1);
    }

    private async Task<Seed> SeedAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var scope = _fixture.BeginScopeAs(userId, teamId);
        var workflowId = await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand { Name = $"authority-{Guid.NewGuid():N}", Definition = WorkflowsTestSeed.MinimalDefinition(), Activations = [], Enabled = true });
        return new Seed(teamId, userId, workflowId);
    }

    private async Task RevokeAsync(Seed seed)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().TeamMembership.Where(m => m.TeamId == seed.TeamId && m.UserId == seed.UserId).ExecuteDeleteAsync();
    }

    private static void AssertAuthorityFailure(Exception exception)
    {
        var failure = exception.ShouldBeAssignableTo<IFailure>();
        failure.Kind.ShouldBe(FailureKind.Forbidden);
        failure.Code.ShouldBe("agent.authority_denied");
    }

    private static AgentTask Task() => new() { Goal = "check authority", Harness = "test", Autonomy = AgentAutonomyLevel.Unleashed, Permissions = AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Unleashed) };
    private static RunSourceEnvelope Manual(Seed seed) => new() { TeamId = seed.TeamId, WorkflowId = seed.WorkflowId, WorkflowVersion = 1, SourceType = WorkflowRunSourceTypes.Manual, ActorType = WorkflowRunActorTypes.User, ActorId = seed.UserId, CreatedBy = seed.UserId, NormalizedPayloadJson = "{}" };
    private sealed record Seed(Guid TeamId, Guid UserId, Guid WorkflowId);
}
