using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Authority;
using CodeSpace.Core.Services.Agents.Authority.Exceptions;
using CodeSpace.Core.Services.Agents.Review;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

public sealed partial class AgentExecutionAuthorityFlowTests
{
    [Theory]
    [InlineData("author", false)]
    [InlineData("author", true)]
    [InlineData("launcher", false)]
    [InlineData("launcher", true)]
    [InlineData("publisher", false)]
    [InlineData("publisher", true)]
    public async Task Review_delegation_rechecks_each_parent_authority_subject(string revoked, bool alreadyAdmitted)
    {
        var seed = await SeedAsync();
        var otherActor = await AddMemberAsync(seed);
        var source = revoked == "publisher" ? await TriggerAsync(seed with { UserId = otherActor }) : Manual(seed) with { ActorId = otherActor, CreatedBy = otherActor };
        using var background = _fixture.BeginScope();
        var db = background.Resolve<CodeSpaceDbContext>();
        var provider = new ProviderInstance { Id = Guid.NewGuid(), TeamId = seed.TeamId, Provider = ProviderKind.GitHub, DisplayName = "review", BaseUrl = "https://example.test" };
        var repository = new Repository { Id = Guid.NewGuid(), TeamId = seed.TeamId, ProviderInstanceId = provider.Id, ExternalId = Guid.NewGuid().ToString(), NamespacePath = "team", Name = "repo", FullPath = "team/repo", WebUrl = "https://example.test/repo" };
        db.ProviderInstance.Add(provider);
        db.Repository.Add(repository);
        await db.SaveChangesAsync();
        var workflowId = await background.Resolve<IRunStarter>().StartAsync(source, CancellationToken.None);
        var runs = background.Resolve<IAgentRunService>();
        var parent = await runs.CreateAsync(Task() with { RepositoryId = repository.Id }, seed.TeamId, workflowId, "producer", cancellationToken: CancellationToken.None);
        var owner = (await runs.ClaimOwnershipAsync(parent.Id, CancellationToken.None))!;
        var review = AgentReviewRunner.BuildReviewTask(new AgentReviewSpec { ParentOwner = owner, RepositoryId = repository.Id, TeamId = seed.TeamId, SubjectInstructions = "inspect", IterationKey = "#review" }, "test");
        var request = new AgentReviewCreation(owner, seed.TeamId, review);
        AgentRun? child = null;
        if (alreadyAdmitted)
        {
            child = await runs.CreateReviewAsync(request, CancellationToken.None);
            child.WorkflowRunId.ShouldBe(workflowId);
            var receipt = JsonSerializer.Deserialize<AgentTask>(child.TaskJson, AgentJson.Options)!.ExecutionAuthority!;
            receipt.Subjects.Select(s => s.UserId).Distinct().Order().ShouldBe(new[] { seed.UserId, otherActor }.Order());
            (await runs.ClaimOwnershipAsync(child.Id, CancellationToken.None)).ShouldNotBeNull();
            await background.Resolve<ExecutionAuthorityService>().EnsureAgentActionAsync(child.Id, seed.TeamId, CancellationToken.None);
        }
        var userId = revoked == "author" ? seed.UserId : otherActor;
        await db.TeamMembership.Where(m => m.TeamId == seed.TeamId && m.UserId == userId).ExecuteDeleteAsync();
        if (child is null) await Should.ThrowAsync<AgentAuthorityDeniedException>(() => runs.CreateReviewAsync(request, CancellationToken.None));
        else await Should.ThrowAsync<AgentAuthorityDeniedException>(() => background.Resolve<ExecutionAuthorityService>().EnsureAgentActionAsync(child.Id, seed.TeamId, CancellationToken.None));
        (await db.TeamMembership.CountAsync(m => m.TeamId == seed.TeamId)).ShouldBe(1, "the other authorized principal is still present, so this proves intersection");
    }
}
