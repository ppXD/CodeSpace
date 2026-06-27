using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Persistence;

/// <summary>
/// Pins the <c>agent_skill_binding</c> unique-pair guarantee at the DB tier (Rule 9 — integration tests own
/// unique constraints). The service's diff logic AVOIDS ever attempting a duplicate insert, so only a direct
/// two-row insert proves the migration's <c>uq_agent_skill_binding_pair</c> would reject a double-bind if any
/// path (e.g. the future importer) bypassed the service. Mirrors the constraint tests for the sibling unique
/// indexes (AgentDefinition / SkillDefinition persistence tests).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AgentSkillBindingPersistenceTests
{
    private readonly PostgresFixture _fixture;

    public AgentSkillBindingPersistenceTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_skill_binds_to_an_agent_at_most_once()
    {
        var teamId = await SeedTeamAsync();
        var agentId = await InsertAgentAsync(teamId, "reviewer");
        var skillId = await InsertSkillAsync(teamId, "tdd");

        await InsertBindingAsync(agentId, skillId);

        // The same (agent, skill) pair a second time violates uq_agent_skill_binding_pair.
        var ex = await Should.ThrowAsync<DbUpdateException>(() => InsertBindingAsync(agentId, skillId));
        (ex.InnerException as Npgsql.PostgresException)!.SqlState.ShouldBe("23505");
        (ex.InnerException as Npgsql.PostgresException)!.ConstraintName.ShouldContain("uq_agent_skill_binding_pair");
    }

    private async Task InsertBindingAsync(Guid agentId, Guid skillId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.AgentSkillBinding.Add(new AgentSkillBinding { Id = Guid.NewGuid(), AgentDefinitionId = agentId, SkillDefinitionId = skillId, CreatedDate = DateTimeOffset.UtcNow, CreatedBy = Guid.NewGuid() });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> InsertAgentAsync(Guid teamId, string slug)
    {
        var id = Guid.NewGuid();
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.AgentDefinition.Add(new AgentDefinition { Id = id, TeamId = teamId, Slug = slug, Name = slug, Origin = AgentDefinitionOrigin.Authored, CreatedDate = DateTimeOffset.UtcNow, CreatedBy = Guid.NewGuid(), LastModifiedDate = DateTimeOffset.UtcNow, LastModifiedBy = Guid.NewGuid() });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> InsertSkillAsync(Guid teamId, string slug)
    {
        var id = Guid.NewGuid();
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.SkillDefinition.Add(new SkillDefinition { Id = id, TeamId = teamId, Slug = slug, Name = slug, Origin = SkillDefinitionOrigin.Authored, CreatedDate = DateTimeOffset.UtcNow, CreatedBy = Guid.NewGuid(), LastModifiedDate = DateTimeOffset.UtcNow, LastModifiedBy = Guid.NewGuid() });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"bind-{userId:N}@test.local", Name = $"bind-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"bind-{teamId:N}", Name = "Bind Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return teamId;
    }
}
