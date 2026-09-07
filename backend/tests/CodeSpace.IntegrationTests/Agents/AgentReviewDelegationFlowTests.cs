using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Authority;
using CodeSpace.Core.Services.Agents.Authority.Exceptions;
using CodeSpace.Core.Services.Agents.Exceptions;
using CodeSpace.Core.Services.Agents.Review;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AgentReviewDelegationFlowTests
{
    private readonly PostgresFixture _fixture;
    public AgentReviewDelegationFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_background_reviewer_uses_the_live_parent_grant_and_keeps_its_own_identity()
    {
        var seed = await SeedAsync();
        using var background = _fixture.BeginScope();
        var runs = background.Resolve<IAgentRunService>();
        var child = await runs.CreateReviewAsync(Request(seed), CancellationToken.None);
        var task = ReadTask(child);
        task.ExecutionAuthority!.SourceKind.ShouldBe("agent-review");
        task.ExecutionAuthority.LogicalRunId.ShouldBe(child.Id);
        task.ExecutionAuthority.Subjects.Single().UserId.ShouldBe(seed.UserId);
        task.ExecutionAuthority.GrantedCeiling.ShouldBe(AgentAutonomyLevel.Confined);
        task.Permissions.WriteScope.ShouldBe(AgentWriteScope.ReadOnly);
        task.EnableMcpEndpoint.ShouldBe(false);
        using var json = JsonDocument.Parse(child.TaskJson);
        json.RootElement.GetProperty("executionAuthority").GetProperty("parentAgentRunId").GetGuid().ShouldBe(seed.Owner.RunId);
        child.TaskJson.ShouldNotContain(seed.Owner.OwnerId.ToString());
        child.WorkflowRunId.ShouldBeNull();
        child.NodeId.ShouldBe("producer");
        child.IterationKey.ShouldBe("round-1#review");
        (await runs.ClaimOwnershipAsync(child.Id, CancellationToken.None)).ShouldNotBeNull();
        await background.Resolve<ExecutionAuthorityService>().EnsureAgentActionAsync(child.Id, seed.TeamId, CancellationToken.None);
    }

    [Theory]
    [InlineData("uuid")]
    [InlineData("epoch")]
    [InlineData("expired")]
    [InlineData("terminal")]
    public async Task Parent_ownership_must_belong_to_this_invocation(string fault)
    {
        var seed = await SeedAsync();
        using var actor = _fixture.BeginScopeAs(seed.UserId, seed.TeamId);
        var db = actor.Resolve<CodeSpaceDbContext>();
        var owner = seed.Owner;
        if (fault == "uuid") owner = owner with { OwnerId = Guid.NewGuid() };
        if (fault == "epoch") owner = owner with { Epoch = owner.Epoch + 1 };
        if (fault == "expired") await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET lease_expires_at = clock_timestamp() - interval '1 second' WHERE id = {owner.RunId}");
        if (fault == "terminal") await db.AgentRun.Where(r => r.Id == owner.RunId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, AgentRunStatus.Cancelled));
        await Should.ThrowAsync<AgentRunOwnershipLostException>(() => actor.Resolve<IAgentRunService>().CreateReviewAsync(Request(seed) with { ParentOwner = owner }, CancellationToken.None));
        (await db.AgentRun.CountAsync(r => r.TeamId == seed.TeamId)).ShouldBe(1);
    }

    [Fact]
    public async Task A_valid_owner_token_cannot_delegate_into_another_team()
    {
        var seed = await SeedAsync();
        var other = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var actor = _fixture.BeginScopeAs(other.UserId, other.TeamId);
        await Should.ThrowAsync<AgentAuthorityDeniedException>(() => actor.Resolve<IAgentRunService>().CreateReviewAsync(Request(seed) with { TeamId = other.TeamId }, CancellationToken.None));
        (await actor.Resolve<CodeSpaceDbContext>().AgentRun.CountAsync(r => r.TeamId == other.TeamId)).ShouldBe(0);
    }

    [Theory]
    [InlineData("autonomy")]
    [InlineData("network")]
    [InlineData("write")]
    [InlineData("publish")]
    [InlineData("mcp")]
    [InlineData("tools")]
    [InlineData("repository")]
    [InlineData("workspace")]
    [InlineData("local-path")]
    [InlineData("recursion")]
    [InlineData("environment")]
    [InlineData("runner")]
    public async Task A_delegated_reviewer_cannot_widen_its_parent_or_readonly_scope(string fault)
    {
        var seed = await SeedAsync();
        var task = ReviewTask(seed.RepositoryId);
        task = fault switch
        {
            "autonomy" => task with { Autonomy = AgentAutonomyLevel.Trusted },
            "network" => task with { Permissions = task.Permissions with { Network = AgentNetworkAccess.On } },
            "write" => task with { Permissions = task.Permissions with { WriteScope = AgentWriteScope.Workspace } },
            "publish" => task with { PushProducedBranch = true },
            "mcp" => task with { EnableMcpEndpoint = true },
            "tools" => task with { Tools = null },
            "repository" => task with { RepositoryId = Guid.NewGuid() },
            "workspace" => task with { Workspace = WorkspaceSpec.FromRepository(Guid.NewGuid()) },
            "local-path" => task with { WorkspaceDirectory = "/private/unrelated" },
            "recursion" => task with { ReviewerAgent = true },
            "environment" => task with { Environment = new Dictionary<string, string> { ["HOME"] = "/private/unrelated" } },
            "runner" => task with { RunnerKind = "untrusted-runner" },
            _ => throw new InvalidOperationException(),
        };
        using var actor = _fixture.BeginScopeAs(seed.UserId, seed.TeamId);
        await Should.ThrowAsync<AgentAuthorityDeniedException>(() => actor.Resolve<IAgentRunService>().CreateReviewAsync(Request(seed) with { Task = task }, CancellationToken.None));
        (await actor.Resolve<CodeSpaceDbContext>().AgentRun.CountAsync(r => r.TeamId == seed.TeamId)).ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_revoked_launcher_cannot_authorize_admission_or_a_persisted_child_action(bool alreadyAdmitted)
    {
        var seed = await SeedAsync();
        using var background = _fixture.BeginScope();
        var runs = background.Resolve<IAgentRunService>();
        var child = alreadyAdmitted ? await runs.CreateReviewAsync(Request(seed), CancellationToken.None) : null;
        var db = background.Resolve<CodeSpaceDbContext>();
        await db.TeamMembership.Where(m => m.TeamId == seed.TeamId && m.UserId == seed.UserId).ExecuteDeleteAsync();
        if (child == null) await Should.ThrowAsync<AgentAuthorityDeniedException>(() => runs.CreateReviewAsync(Request(seed), CancellationToken.None));
        else
        {
            await Should.ThrowAsync<AgentAuthorityDeniedException>(() => runs.ClaimOwnershipAsync(child.Id, CancellationToken.None));
            await Should.ThrowAsync<AgentAuthorityDeniedException>(() => background.Resolve<ExecutionAuthorityService>().EnsureAgentActionAsync(child.Id, seed.TeamId, CancellationToken.None));
            (await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == child.Id)).Status.ShouldBe(AgentRunStatus.Queued);
        }
    }

    [Theory]
    [InlineData("terminal")]
    [InlineData("receipt")]
    [InlineData("repository-deleted")]
    public async Task Persisted_children_revalidate_parent_and_repository_from_the_database(string fault)
    {
        var seed = await SeedAsync();
        using var background = _fixture.BeginScope();
        var runs = background.Resolve<IAgentRunService>();
        var child = await runs.CreateReviewAsync(Request(seed), CancellationToken.None);
        var db = background.Resolve<CodeSpaceDbContext>();
        if (fault == "terminal") await db.AgentRun.Where(r => r.Id == seed.Owner.RunId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, AgentRunStatus.Cancelled));
        if (fault == "receipt") await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET task_jsonb = jsonb_set(task_jsonb, '{{executionAuthority,issuedAt}}', to_jsonb(clock_timestamp())) WHERE id = {seed.Owner.RunId}");
        if (fault == "repository-deleted") await db.Repository.Where(r => r.Id == seed.RepositoryId).ExecuteUpdateAsync(s => s.SetProperty(r => r.DeletedDate, DateTimeOffset.UtcNow));
        await Should.ThrowAsync<AgentAuthorityDeniedException>(() => background.Resolve<ExecutionAuthorityService>().EnsureAgentActionAsync(child.Id, seed.TeamId, CancellationToken.None));
    }

    [Fact]
    public async Task Observer_replacement_does_not_forge_a_new_delegation_or_revoke_the_same_logical_parent()
    {
        var seed = await SeedAsync();
        using var background = _fixture.BeginScope();
        var runs = background.Resolve<IAgentRunService>();
        var child = await runs.CreateReviewAsync(Request(seed), CancellationToken.None);
        var db = background.Resolve<CodeSpaceDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET lease_expires_at = clock_timestamp() - interval '1 second' WHERE id = {seed.Owner.RunId}");
        var reservation = (await runs.ReserveReattachAsync(seed.Owner.RunId, CancellationToken.None))!;
        (await runs.ActivateReattachAsync(reservation, CancellationToken.None)).ShouldNotBeNull();
        await background.Resolve<ExecutionAuthorityService>().EnsureAgentActionAsync(child.Id, seed.TeamId, CancellationToken.None);
        await Should.ThrowAsync<AgentRunOwnershipLostException>(() => runs.CreateReviewAsync(Request(seed), CancellationToken.None));
    }

    [Fact]
    public async Task Ordinary_creation_never_accepts_a_serialized_delegation_as_a_grant()
    {
        var seed = await SeedAsync();
        AgentTask forged;
        using (var background = _fixture.BeginScope())
        {
            var child = await background.Resolve<IAgentRunService>().CreateReviewAsync(Request(seed), CancellationToken.None);
            forged = ReadTask(child);
            await Should.ThrowAsync<AgentAuthorityDeniedException>(() => background.Resolve<IAgentRunService>().CreateAsync(forged, seed.TeamId, null, null, cancellationToken: CancellationToken.None));
        }
        using var actor = _fixture.BeginScopeAs(seed.UserId, seed.TeamId);
        var ordinary = await actor.Resolve<IAgentRunService>().CreateAsync(forged, seed.TeamId, null, null, cancellationToken: CancellationToken.None);
        var receipt = ReadTask(ordinary).ExecutionAuthority!;
        receipt.SourceKind.ShouldBe("standalone");
        receipt.LogicalRunId.ShouldBe(ordinary.Id);
        ordinary.TaskJson.ShouldNotContain("parentAgentRunId");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_review_runner_never_turns_authority_or_ownership_refusal_into_model_fallback(bool revoked)
    {
        var seed = await SeedAsync();
        using var background = _fixture.BeginScope();
        if (revoked) await background.Resolve<CodeSpaceDbContext>().TeamMembership.Where(m => m.TeamId == seed.TeamId && m.UserId == seed.UserId).ExecuteDeleteAsync();
        var spec = new AgentReviewSpec { ParentOwner = revoked ? seed.Owner : seed.Owner with { OwnerId = Guid.NewGuid() }, RepositoryId = seed.RepositoryId, TeamId = seed.TeamId, SubjectInstructions = "inspect", IterationKey = "#review" };
        if (revoked) await Should.ThrowAsync<AgentAuthorityDeniedException>(() => background.Resolve<AgentReviewRunner>().RunAsync(spec, CancellationToken.None));
        else await Should.ThrowAsync<AgentRunOwnershipLostException>(() => background.Resolve<AgentReviewRunner>().RunAsync(spec, CancellationToken.None));
        (await background.Resolve<CodeSpaceDbContext>().AgentRun.CountAsync(r => r.TeamId == seed.TeamId)).ShouldBe(1);
    }

    [Fact]
    public async Task Lease_loss_at_child_persistence_rolls_back_admission_instead_of_returning_a_queued_child()
    {
        var seed = await SeedAsync();
        var fault = new ExpireAfterChildPersist(seed.Owner.RunId);
        using var background = _fixture.BeginScope(builder =>
        {
            var options = new DbContextOptionsBuilder<CodeSpaceDbContext>().UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention().AddInterceptors(fault).Options;
            builder.RegisterInstance(options).As<DbContextOptions<CodeSpaceDbContext>>().SingleInstance();
        });
        await Should.ThrowAsync<AgentRunOwnershipLostException>(() => background.Resolve<IAgentRunService>().CreateReviewAsync(Request(seed), CancellationToken.None));
        fault.Fired.ShouldBeTrue("the lease was lost after the actual INSERT, not before the admission check");
        (await background.Resolve<CodeSpaceDbContext>().AgentRun.CountAsync(r => r.TeamId == seed.TeamId)).ShouldBe(1);
        var retry = await background.Resolve<IAgentRunService>().CreateReviewAsync(Request(seed), CancellationToken.None);
        retry.Status.ShouldBe(AgentRunStatus.Queued);
        (await background.Resolve<CodeSpaceDbContext>().AgentRun.CountAsync(r => r.TeamId == seed.TeamId)).ShouldBe(2);
    }

    [Fact]
    public async Task Delegation_does_not_commit_or_discard_unrelated_pending_caller_changes()
    {
        var seed = await SeedAsync();
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var provider = new ProviderInstance { Id = Guid.NewGuid(), TeamId = seed.TeamId, Provider = ProviderKind.GitHub, DisplayName = "pending caller work", BaseUrl = "https://pending-caller.example.test" };
        db.ProviderInstance.Add(provider);
        await Should.ThrowAsync<InvalidOperationException>(() => scope.Resolve<IAgentRunService>().CreateReviewAsync(Request(seed), CancellationToken.None));
        db.Entry(provider).State.ShouldBe(EntityState.Added);
        (await db.AgentRun.CountAsync(r => r.TeamId == seed.TeamId)).ShouldBe(1);
        using var independent = _fixture.BeginScope();
        (await independent.Resolve<CodeSpaceDbContext>().ProviderInstance.AnyAsync(p => p.Id == provider.Id)).ShouldBeFalse();
    }

    private sealed class ExpireAfterChildPersist(Guid parentId) : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (!Fired)
            {
                Fired = true;
                await eventData.Context!.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET lease_expires_at = clock_timestamp() - interval '1 second' WHERE id = {parentId}", cancellationToken);
            }
            return result;
        }
    }

    private async Task<Seed> SeedAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var actor = _fixture.BeginScopeAs(userId, teamId);
        var db = actor.Resolve<CodeSpaceDbContext>();
        var provider = new ProviderInstance { Id = Guid.NewGuid(), TeamId = teamId, Provider = ProviderKind.GitHub, DisplayName = "delegation", BaseUrl = "https://example.test" };
        var repository = new Repository { Id = Guid.NewGuid(), TeamId = teamId, ProviderInstanceId = provider.Id, ExternalId = Guid.NewGuid().ToString(), NamespacePath = "team", Name = "repo", FullPath = "team/repo", WebUrl = "https://example.test/team/repo" };
        db.ProviderInstance.Add(provider);
        db.Repository.Add(repository);
        await db.SaveChangesAsync();
        var runs = actor.Resolve<IAgentRunService>();
        var parent = await runs.CreateAsync(new AgentTask { Goal = "produce", Harness = "test", RepositoryId = repository.Id, Tools = ["Read"], ReviewerAgent = true }, teamId, null, "producer", "round-1", CancellationToken.None);
        return new Seed(teamId, userId, repository.Id, (await runs.ClaimOwnershipAsync(parent.Id, CancellationToken.None))!);
    }

    private static AgentReviewCreation Request(Seed seed) => new(seed.Owner, seed.TeamId, ReviewTask(seed.RepositoryId));
    private static AgentTask ReviewTask(Guid repositoryId) => new()
    {
        Goal = "inspect", Harness = "review", RepositoryId = repositoryId, Tools = ["Read"], Autonomy = AgentAutonomyLevel.Confined,
        Permissions = AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Confined), EnableMcpEndpoint = false, PushProducedBranch = false,
        OutputReviewMode = ReviewMode.None, ReviewerAgent = false, MaxReviseRounds = 0,
        Workspace = new WorkspaceSpec { Repositories = [new WorkspaceRepositorySpec { RepositoryId = repositoryId, Alias = "repo", Access = WorkspaceAccess.Read, IsPrimary = true }] },
    };
    private static AgentTask ReadTask(AgentRun run) => JsonSerializer.Deserialize<AgentTask>(run.TaskJson, AgentJson.Options)!;
    private sealed record Seed(Guid TeamId, Guid UserId, Guid RepositoryId, AgentRunOwnerToken Owner);
}
