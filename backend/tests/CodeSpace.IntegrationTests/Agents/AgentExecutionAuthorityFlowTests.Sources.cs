using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

public sealed partial class AgentExecutionAuthorityFlowTests
{
    [Fact]
    public async Task Revoking_the_author_stops_execution_even_when_the_different_launcher_still_has_permission()
    {
        var seed = await SeedAsync();
        var launcher = await AddMemberAsync(seed);
        Guid agentRunId;
        using (var scope = _fixture.BeginScopeAs(launcher, seed.TeamId))
        {
            var runId = await scope.Resolve<IRunStarter>().StartAsync(Manual(seed) with { ActorId = launcher, CreatedBy = launcher }, CancellationToken.None);
            agentRunId = (await scope.Resolve<IAgentRunService>().CreateAsync(Task(), seed.TeamId, runId, "agent", cancellationToken: CancellationToken.None)).Id;
        }
        await RevokeAsync(seed);
        using var claim = _fixture.BeginScope();
        AssertAuthorityFailure(await Should.ThrowAsync<Exception>(() => claim.Resolve<IAgentRunService>().MarkRunningAsync(agentRunId, CancellationToken.None)));
        (await claim.Resolve<CodeSpaceDbContext>().TeamMembership.AnyAsync(m => m.TeamId == seed.TeamId && m.UserId == launcher)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_fake_current_user_admin_claim_does_not_replace_actual_database_standing()
    {
        var seed = await SeedAsync();
        await RevokeAsync(seed);
        using var fakeAdmin = _fixture.BeginScopeAs(seed.UserId, seed.TeamId, Roles.Admin);
        AssertAuthorityFailure(await Should.ThrowAsync<Exception>(() => fakeAdmin.Resolve<IAgentRunService>().CreateAsync(Task(), seed.TeamId, null, null, cancellationToken: CancellationToken.None)));
    }

    [Fact]
    public async Task A_real_database_admin_keeps_existing_standing_and_loses_it_when_the_role_is_revoked()
    {
        var seed = await SeedAsync();
        await RevokeAsync(seed);
        Guid roleAssignmentId, agentRunId;
        using (var setup = _fixture.BeginScopeAs(seed.UserId, seed.TeamId))
        {
            var db = setup.Resolve<CodeSpaceDbContext>();
            roleAssignmentId = Guid.NewGuid();
            db.RoleUser.Add(new RoleUser { Id = roleAssignmentId, RoleId = await db.Role.Where(r => r.Name == Roles.Admin).Select(r => r.Id).SingleAsync(), UserId = seed.UserId });
            await db.SaveChangesAsync();
            agentRunId = (await setup.Resolve<IAgentRunService>().CreateAsync(Task(), seed.TeamId, null, null, cancellationToken: CancellationToken.None)).Id;
        }
        using var runtime = _fixture.BeginScope();
        await runtime.Resolve<CodeSpaceDbContext>().RoleUser.Where(r => r.Id == roleAssignmentId).ExecuteDeleteAsync();
        AssertAuthorityFailure(await Should.ThrowAsync<Exception>(() => runtime.Resolve<IAgentRunService>().MarkRunningAsync(agentRunId, CancellationToken.None)));
    }

    [Fact]
    public async Task A_bot_membership_cannot_be_mistaken_for_an_operator_principal()
    {
        var seed = await SeedAsync();
        using var scope = _fixture.BeginScopeAs(seed.UserId, seed.TeamId);
        await scope.Resolve<CodeSpaceDbContext>().User.Where(u => u.Id == seed.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsBot, true));
        AssertAuthorityFailure(await Should.ThrowAsync<Exception>(() => scope.Resolve<IAgentRunService>().CreateAsync(Task(), seed.TeamId, null, null, cancellationToken: CancellationToken.None)));
    }

    [Fact]
    public async Task A_child_inherits_the_parent_ceiling_and_cannot_use_a_foreign_team_parent()
    {
        var seed = await SeedAsync();
        var parentId = await SnapshotAsync(seed, AgentAutonomyLevel.Standard);
        using var scope = _fixture.BeginScope();
        var childSource = Manual(seed) with { SourceType = WorkflowRunSourceTypes.ChildWorkflow, ActorType = WorkflowRunActorTypes.System, ActorId = SystemUsers.SeederId, ParentRunId = parentId };
        var childId = await scope.Resolve<IRunStarter>().StartAsync(childSource, CancellationToken.None);
        var task = JsonSerializer.Deserialize<AgentTask>((await scope.Resolve<IAgentRunService>().CreateAsync(Task(), seed.TeamId, childId, "agent", cancellationToken: CancellationToken.None)).TaskJson, AgentJson.Options)!;
        task.Autonomy.ShouldBe(AgentAutonomyLevel.Standard);
        task.Permissions.Network.ShouldBe(AgentNetworkAccess.Off);
        var other = await SeedAsync();
        using var foreign = _fixture.BeginScope();
        AssertAuthorityFailure(await Should.ThrowAsync<Exception>(() => foreign.Resolve<IRunStarter>().StartAsync(childSource with { TeamId = other.TeamId, WorkflowId = other.WorkflowId }, CancellationToken.None)));
    }

    [Fact]
    public async Task Replay_mints_the_new_actor_and_retains_the_ceiling_after_original_actor_revocation_and_cancel()
    {
        var seed = await SeedAsync();
        var newActor = await AddMemberAsync(seed);
        var originalId = await SnapshotAsync(seed, AgentAutonomyLevel.Standard);
        await RevokeAsync(seed);
        using var scope = _fixture.BeginScopeAs(newActor, seed.TeamId);
        var db = scope.Resolve<CodeSpaceDbContext>();
        await db.WorkflowRun.Where(r => r.Id == originalId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, WorkflowRunStatus.Cancelled));
        var original = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == originalId);
        var replayId = await scope.Resolve<IRunFromSnapshotStarter>().StageReplayFromSnapshotAsync(original.DefinitionSnapshotJson!, original.DefinitionSnapshotHash!, seed.TeamId, newActor, "{}", WorkflowRunSourceTypes.Replay, originalId, originalId, null, original.RunRequestId, [], [], original.ProjectionKind, null, null, CancellationToken.None);
        var task = JsonSerializer.Deserialize<AgentTask>((await scope.Resolve<IAgentRunService>().CreateAsync(Task(), seed.TeamId, replayId, "agent", cancellationToken: CancellationToken.None)).TaskJson, AgentJson.Options)!;
        task.Autonomy.ShouldBe(AgentAutonomyLevel.Standard);
        task.ExecutionAuthority!.Subjects.ShouldHaveSingleItem().UserId.ShouldBe(newActor);
        task.ExecutionAuthority.LogicalRunId.ShouldBe(replayId);
    }

    [Fact]
    public async Task A_verifiable_legacy_manual_run_upgrades_without_changing_the_engine_run_xmin()
    {
        var seed = await SeedAsync();
        var legacyRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, seed.WorkflowId, seed.TeamId);
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var tracked = await db.WorkflowRun.SingleAsync(r => r.Id == legacyRunId);
        var originalXmin = tracked.Xmin;
        await scope.Resolve<IAgentRunService>().CreateAsync(Task(), seed.TeamId, legacyRunId, "agent", cancellationToken: CancellationToken.None);
        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == legacyRunId)).Xmin.ShouldBe(originalXmin);
        (await db.WorkflowRunExecutionAuthority.CountAsync(r => r.WorkflowRunId == legacyRunId)).ShouldBe(1);
    }

    [Fact]
    public async Task An_unknown_legacy_author_requires_republication_instead_of_borrowing_the_current_launcher()
    {
        var seed = await SeedAsync();
        using (var setup = _fixture.BeginScope())
        {
            var db = setup.Resolve<CodeSpaceDbContext>();
            var version = await db.WorkflowVersion.AsNoTracking().SingleAsync(v => v.WorkflowId == seed.WorkflowId);
            db.WorkflowVersion.Add(new WorkflowVersion { WorkflowId = seed.WorkflowId, Version = 2, DefinitionJson = version.DefinitionJson, DefinitionHash = version.DefinitionHash, CommittedAt = DateTimeOffset.UtcNow, CreatedDate = DateTimeOffset.UtcNow, CreatedBy = Guid.Empty });
            await db.SaveChangesAsync();
        }
        var legacyRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, seed.WorkflowId, seed.TeamId, workflowVersion: 2);
        using var runtime = _fixture.BeginScopeAs(seed.UserId, seed.TeamId);
        var exception = await Should.ThrowAsync<Exception>(() => runtime.Resolve<IAgentRunService>().CreateAsync(Task(), seed.TeamId, legacyRunId, "agent", cancellationToken: CancellationToken.None));
        AssertAuthorityFailure(exception);
        exception.Message.ShouldContain("republish");
        (await runtime.Resolve<CodeSpaceDbContext>().AgentRun.CountAsync(r => r.TeamId == seed.TeamId)).ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_database_rejects_a_receipt_with_a_foreign_or_null_json_team_identity(bool nullTeam)
    {
        var seed = await SeedAsync();
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, seed.WorkflowId, seed.TeamId);
        using var scope = _fixture.BeginScope();
        var json = JsonSerializer.Serialize(new { logicalRunId = runId, teamId = nullTeam ? (Guid?)null : Guid.NewGuid() });
        var exception = await Should.ThrowAsync<Npgsql.PostgresException>(() => scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"INSERT INTO workflow_run_execution_authority (workflow_run_id, team_id, receipt_json, issued_at) VALUES ({runId}, {seed.TeamId}, {json}::jsonb, now())"));
        exception.SqlState.ShouldBe("23514");
    }

    [Fact]
    public async Task A_legacy_replay_cannot_reconstruct_its_missing_historical_ceiling_from_current_deployment()
    {
        var seed = await SeedAsync();
        var originalId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, seed.WorkflowId, seed.TeamId);
        using var scope = _fixture.BeginScopeAs(seed.UserId, seed.TeamId);
        var db = scope.Resolve<CodeSpaceDbContext>();
        var original = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == originalId);
        var source = Manual(seed) with { SourceType = WorkflowRunSourceTypes.Replay, ParentRunId = originalId, CausationRequestId = original.RunRequestId, ReleaseHashAtRun = await db.WorkflowVersion.Where(v => v.WorkflowId == seed.WorkflowId && v.Version == 1).Select(v => v.DefinitionHash).SingleAsync() };
        var exception = await Should.ThrowAsync<Exception>(() => scope.Resolve<IRunStarter>().StartAsync(source, CancellationToken.None));
        AssertAuthorityFailure(exception);
        exception.Message.ShouldContain("legacy-replay-authority-unverifiable");
        (await db.WorkflowRun.CountAsync(r => r.TeamId == seed.TeamId)).ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_trigger_timestamps_do_not_prove_an_immutable_delegation(bool sameTimestamp)
    {
        var seed = await SeedAsync();
        var source = await TriggerAsync(seed);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, seed.WorkflowId, seed.TeamId);
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var receivedAt = DateTimeOffset.UtcNow.AddHours(1);
        await db.WorkflowRun.Where(r => r.Id == runId).ExecuteUpdateAsync(s => s.SetProperty(r => r.SourceType, WorkflowRunSourceTypes.ScheduleCron));
        var requestId = await db.WorkflowRun.Where(r => r.Id == runId).Select(r => r.RunRequestId).SingleAsync();
        await db.WorkflowRunRequest.Where(r => r.Id == requestId).ExecuteUpdateAsync(s => s.SetProperty(r => r.SourceType, WorkflowRunSourceTypes.ScheduleCron).SetProperty(r => r.ActorType, WorkflowRunActorTypes.System).SetProperty(r => r.ActorId, SystemUsers.SeederId).SetProperty(r => r.ActivationId, source.ActivationId).SetProperty(r => r.ActivationSnapshotJson, source.ActivationSnapshotJson).SetProperty(r => r.ReceivedAt, receivedAt));
        if (sameTimestamp) await db.WorkflowActivation.Where(a => a.Id == source.ActivationId).ExecuteUpdateAsync(s => s.SetProperty(a => a.LastModifiedDate, receivedAt));
        var exception = await Should.ThrowAsync<Exception>(() => scope.Resolve<IAgentRunService>().CreateAsync(Task(), seed.TeamId, runId, "agent", cancellationToken: CancellationToken.None));
        AssertAuthorityFailure(exception);
        exception.Message.ShouldContain("legacy-trigger-authority-unverifiable");
        (await db.WorkflowRunExecutionAuthority.CountAsync(r => r.WorkflowRunId == runId)).ShouldBe(0);
    }

    private async Task<Guid> AddMemberAsync(Seed seed)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"authority-member-{userId:N}@test.local", Name = "Authorized member" });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = seed.TeamId, UserId = userId, Role = TeamRole.Member });
        await db.SaveChangesAsync();
        return userId;
    }

    private async Task<Guid> SnapshotAsync(Seed seed, AgentAutonomyLevel ceiling)
    {
        using var scope = _fixture.BeginScopeAs(seed.UserId, seed.TeamId);
        var definition = WorkflowsTestSeed.MinimalDefinition() with { LaunchContract = new TaskLaunchContract { Version = 1, Goal = "bounded task", SurfaceKind = "chat", RequestedControls = new TaskLaunchControls { Autonomy = ceiling.ToString() }, ResolvedRoute = new RoutePlan { ProjectionKind = "single-agent", Caps = new RouteCaps { AutonomyCeiling = ceiling.ToString() }, EffectiveAutonomy = ceiling.ToString() } } };
        return await scope.Resolve<IRunFromSnapshotStarter>().StartFromSnapshotAsync(definition, seed.TeamId, seed.UserId, "{}", [], "single-agent", null, CancellationToken.None);
    }
}
