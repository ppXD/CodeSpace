using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// The import WRITE path (<see cref="IAgentDefinitionService.ImportAsync"/>) against real Postgres: an
/// imported persona persists with Origin=Imported + provenance (SourcePath) + the verbatim frontmatter +
/// tools tri-state; the slug guard rejects a collision exactly like an authored create; and the
/// authored-vs-import boundary holds — an Update of an imported persona keeps its provenance intact.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AgentImportWriteTests
{
    private readonly PostgresFixture _fixture;

    public AgentImportWriteTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Import_persists_an_imported_persona_with_provenance_and_verbatim_frontmatter()
    {
        var (teamId, userId) = await SeedTeamAsync();

        Guid id;
        using (var scope = _fixture.BeginScope())
            id = await scope.Resolve<IAgentDefinitionService>().ImportAsync(teamId, new ImportedAgentDefinitionInput
            {
                Name = "Backend Architect",
                Description = "Use PROACTIVELY for system design.",
                SystemPrompt = "You are a senior backend architect.",
                Model = "claude-opus-4-8",
                Tools = new[] { "Read", "Grep" },
                RawFrontmatterJson = "{\"name\":\"backend-architect\",\"custom_future_key\":42}",
                SourcePath = "agents/backend-architect.md",
            }, userId, CancellationToken.None);

        using var verify = _fixture.BeginScope();
        var row = await verify.Resolve<CodeSpaceDbContext>().AgentDefinition.AsNoTracking().SingleAsync(a => a.Id == id);

        row.Origin.ShouldBe(AgentDefinitionOrigin.Imported, "ImportAsync is the only path that produces Origin=Imported");
        row.Slug.ShouldBe("backend-architect", "the @-handle is derived from the name, same as an authored create");
        row.SourcePath.ShouldBe("agents/backend-architect.md");
        row.PackId.ShouldBeNull("v1 imports standalone — no pack table, provenance rides on Origin + SourcePath");
        JsonSerializer.Deserialize<string[]>(row.ToolsJson!).ShouldBe(new[] { "Read", "Grep" });
        JsonDocument.Parse(row.RawFrontmatterJson).RootElement.GetProperty("custom_future_key").GetInt32().ShouldBe(42,
            customMessage: "the verbatim frontmatter is stored as-is — lossless forward-compat survives the write path");
    }

    [Fact]
    public async Task Import_rejects_a_colliding_handle_like_an_authored_create()
    {
        var (teamId, userId) = await SeedTeamAsync();

        using var scope = _fixture.BeginScope();
        var svc = scope.Resolve<IAgentDefinitionService>();

        await svc.ImportAsync(teamId, Input("Code Reviewer", "agents/reviewer.md"), userId, CancellationToken.None);

        // Second import with the same name → same derived handle → the partial unique index fires; the
        // service translates it to the same friendly error an authored create would.
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => svc.ImportAsync(teamId, Input("Code Reviewer", "agents/reviewer-2.md"), userId, CancellationToken.None));
        ex.Message.ShouldContain("code-reviewer", Case.Insensitive);
    }

    [Fact]
    public async Task Editing_an_imported_persona_preserves_its_origin_and_provenance()
    {
        // The authored-vs-import boundary: an Update touches only the editable surface; Origin, SourcePath,
        // and the verbatim frontmatter survive — so a later re-sync against the source still works.
        var (teamId, userId) = await SeedTeamAsync();

        Guid id;
        using (var scope = _fixture.BeginScope())
            id = await scope.Resolve<IAgentDefinitionService>().ImportAsync(teamId, new ImportedAgentDefinitionInput
            {
                Name = "Imported Reviewer",
                SystemPrompt = "imported prompt",
                RawFrontmatterJson = "{\"name\":\"imported-reviewer\",\"keep\":\"me\"}",
                SourcePath = "agents/imported-reviewer.md",
            }, userId, CancellationToken.None);

        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            await scope.Resolve<IMediator>().Send(new UpdateAgentDefinitionCommand { AgentDefinitionId = id, Name = "Imported Reviewer", SystemPrompt = "locally edited" });

        using var verify = _fixture.BeginScope();
        var row = await verify.Resolve<CodeSpaceDbContext>().AgentDefinition.AsNoTracking().SingleAsync(a => a.Id == id);

        row.SystemPrompt.ShouldBe("locally edited", "the authored edit applied");
        row.Origin.ShouldBe(AgentDefinitionOrigin.Imported, "an edit must NOT flip an imported persona to Authored");
        row.SourcePath.ShouldBe("agents/imported-reviewer.md", "provenance survives an edit (re-sync still works)");
        row.RawFrontmatterJson.ShouldContain("keep", customMessage: "the verbatim frontmatter survives an authored edit");
    }

    private static ImportedAgentDefinitionInput Input(string name, string sourcePath) => new()
    {
        Name = name,
        SystemPrompt = "prompt",
        RawFrontmatterJson = "{}",
        SourcePath = sourcePath,
    };

    private async Task<(Guid TeamId, Guid UserId)> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        db.User.Add(new User { Id = userId, Email = $"imp-{userId:N}@test.local", Name = $"imp-{userId:N}", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Team.Add(new Team { Id = teamId, Slug = $"imp-team-{teamId:N}", Name = "Import Team", Kind = TeamKind.Workspace, OwnerUserId = userId, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        await db.SaveChangesAsync();
        return (teamId, userId);
    }
}
