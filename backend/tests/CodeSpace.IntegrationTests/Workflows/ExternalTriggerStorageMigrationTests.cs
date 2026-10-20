using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using Xunit;

namespace CodeSpace.IntegrationTests.Workflows;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ExternalTriggerStorageMigrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task The_slug_is_unique_instance_wide_while_live()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture);
        var workflowId = await SeedWorkflowAsync(teamId, userId);
        var slug = "prod-alerts-" + Guid.NewGuid().ToString("N");
        await SeedAsync(NewRow(teamId, workflowId, userId, slug, live: true));

        await Should.ThrowAsync<DbUpdateException>(() => SeedAsync(NewRow(teamId, workflowId, userId, slug, live: true)));
    }

    [Fact]
    public async Task A_soft_deleted_slug_is_reusable()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture);
        var workflowId = await SeedWorkflowAsync(teamId, userId);
        var slug = "prod-alerts-" + Guid.NewGuid().ToString("N");
        await SeedAsync(NewRow(teamId, workflowId, userId, slug, live: false));
        await SeedAsync(NewRow(teamId, workflowId, userId, slug, live: true));
    }

    private async Task<Guid> SeedWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var workflow = new Workflow { Id = id, TeamId = teamId, Slug = "external-trigger-" + id.ToString("N")[..8], Name = "external-trigger", DefinitionJson = "{}", CreatedDate = now, CreatedBy = userId, LastModifiedDate = now, LastModifiedBy = userId };
        db.Workflow.Add(workflow);
        await db.SaveChangesAsync();
        return workflow.Id;
    }

    private async Task SeedAsync(WorkflowExternalTrigger row)
    {
        using var scope = fixture.BeginScope();
        scope.Resolve<CodeSpaceDbContext>().WorkflowExternalTrigger.Add(row);
        await scope.Resolve<CodeSpaceDbContext>().SaveChangesAsync();
    }

    private static WorkflowExternalTrigger NewRow(Guid teamId, Guid workflowId, Guid userId, string slug, bool live) => new()
    {
        Id = Guid.NewGuid(), TeamId = teamId, WorkflowId = workflowId, NodeId = "notify", Slug = slug,
        KeyDigest = new string('a', 64), KeyCiphertext = "sealed", CreatedDate = DateTimeOffset.UtcNow,
        CreatedBy = userId, LastModifiedDate = DateTimeOffset.UtcNow, LastModifiedBy = userId,
        DeletedDate = live ? null : DateTimeOffset.UtcNow,
    };
}
