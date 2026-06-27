using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Persistence;

/// <summary>
/// Persistence round-trip for the Agent persona (<see cref="AgentDefinition"/>). Proves the jsonb columns
/// (incl. lossless forward-compat of unknown frontmatter keys), the enum-as-string mapping, the xmin
/// concurrency token, and the partial unique index (slug unique per team among non-deleted, reusable after
/// soft-delete). Real Postgres via the migration (Rule 12).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AgentDefinitionPersistenceTests
{
    private readonly PostgresFixture _fixture;

    public AgentDefinitionPersistenceTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Round_trips_all_fields_including_jsonb_enum_and_xmin()
    {
        var teamId = await SeedTeamAsync();
        var id = Guid.NewGuid();

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.AgentDefinition.Add(NewAgent(id, teamId, "backend-architect"));
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var a = await db.AgentDefinition.SingleAsync(x => x.Id == id);

            a.Slug.ShouldBe("backend-architect");
            a.Name.ShouldBe("Backend Architect");
            a.Model.ShouldBeNull();                                  // null = let the harness pick its default
            a.DefaultAutonomy.ShouldBe("guarded");
            a.Origin.ShouldBe(AgentDefinitionOrigin.Imported);       // enum-as-string round-trips

            // jsonb preserves semantics (keys/values + array element order), not byte-exact formatting,
            // so assert structurally rather than on the exact string.
            JsonSerializer.Deserialize<string[]>(a.ToolsJson!).ShouldBe(new[] { "Read", "Grep" });
            JsonDocument.Parse(a.RawFrontmatterJson).RootElement.GetProperty("custom_future_key").GetInt32().ShouldBe(42);  // unknown key survives (forward-compat)
            a.SourcePath.ShouldBe("agents/backend-architect.md");
            a.DeletedDate.ShouldBeNull();
            a.Xmin.ShouldNotBe(0u);                                  // concurrency token populated by Postgres
        }
    }

    [Fact]
    public async Task Slug_is_unique_per_team_among_active_but_reusable_after_soft_delete()
    {
        var teamId = await SeedTeamAsync();

        await InsertAsync(NewAgent(Guid.NewGuid(), teamId, "reviewer"));

        // A second ACTIVE persona with the same (team, slug) violates the partial unique index.
        await Should.ThrowAsync<DbUpdateException>(() => InsertAsync(NewAgent(Guid.NewGuid(), teamId, "reviewer")));

        // Soft-delete the first, then the slug is free to reuse (partial index excludes deleted rows).
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var first = await db.AgentDefinition.SingleAsync(x => x.TeamId == teamId && x.Slug == "reviewer");
            first.DeletedDate = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        await Should.NotThrowAsync(() => InsertAsync(NewAgent(Guid.NewGuid(), teamId, "reviewer")));
    }

    private static AgentDefinition NewAgent(Guid id, Guid teamId, string slug) => new()
    {
        Id = id,
        TeamId = teamId,
        Slug = slug,
        Name = "Backend Architect",
        Description = "Use PROACTIVELY for system design.",
        SystemPrompt = "You are a senior backend architect.",
        Model = null,
        DefaultAutonomy = "guarded",
        ToolsJson = "[\"Read\",\"Grep\"]",
        McpServersJson = "[]",
        RawFrontmatterJson = "{\"name\":\"backend-architect\",\"custom_future_key\":42}",
        Origin = AgentDefinitionOrigin.Imported,
        PackId = Guid.NewGuid(),
        SourcePath = "agents/backend-architect.md",
        CreatedDate = DateTimeOffset.UtcNow,
        CreatedBy = Guid.NewGuid(),
        LastModifiedDate = DateTimeOffset.UtcNow,
        LastModifiedBy = Guid.NewGuid(),
    };

    private async Task InsertAsync(AgentDefinition agent)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.AgentDefinition.Add(agent);
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"agent-{userId:N}@test.local", Name = $"agent-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"agent-{teamId:N}", Name = "Agent Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return teamId;
    }
}
