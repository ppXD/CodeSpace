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
    public async Task A_trigger_is_persisted_without_a_slug()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture);
        var workflowId = await SeedWorkflowAsync(teamId, userId);
        var row = NewRow(teamId, workflowId, userId, live: true);
        await SeedAsync(row);
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var saved = await db.WorkflowExternalTrigger.AsNoTracking().SingleAsync(t => t.Id == row.Id);
        saved.KeyDigest.ShouldBe(row.KeyDigest);
        saved.KeyCiphertext.ShouldBe(row.KeyCiphertext);
        (await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_name = 'workflow_external_trigger' AND column_name = 'slug'").SingleAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task A_new_trigger_does_not_reuse_the_retired_identity()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture);
        var workflowId = await SeedWorkflowAsync(teamId, userId);
        var retired = NewRow(teamId, workflowId, userId, live: false);
        var replacement = NewRow(teamId, workflowId, userId, live: true);
        await SeedAsync(retired);
        await SeedAsync(replacement);
        replacement.Id.ShouldNotBe(retired.Id);
        using var scope = fixture.BeginScope();
        var rows = await scope.Resolve<CodeSpaceDbContext>().WorkflowExternalTrigger.AsNoTracking().Where(t => t.WorkflowId == workflowId).ToArrayAsync();
        rows.Single(t => t.Id == retired.Id).DeletedDate.ShouldNotBeNull();
        rows.Single(t => t.Id == replacement.Id).DeletedDate.ShouldBeNull();
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

    private static WorkflowExternalTrigger NewRow(Guid teamId, Guid workflowId, Guid userId, bool live) => new()
    {
        Id = Guid.NewGuid(), TeamId = teamId, WorkflowId = workflowId, NodeId = "notify",
        KeyDigest = new string('a', 64), KeyCiphertext = "sealed", CreatedDate = DateTimeOffset.UtcNow,
        CreatedBy = userId, LastModifiedDate = DateTimeOffset.UtcNow, LastModifiedBy = userId,
        DeletedDate = live ? null : DateTimeOffset.UtcNow,
    };
}
