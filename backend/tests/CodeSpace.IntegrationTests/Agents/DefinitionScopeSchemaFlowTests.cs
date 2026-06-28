using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Skills;
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
/// The store/snapshot schema foundation (skill-store #5, slice 1) — the additive `scope` + source-link columns
/// on agent_definition / skill_definition. Proves the grandfather floor: anything created through the real write
/// paths defaults to <see cref="DefinitionScope.Working"/> (so it stays on the bench), and the new columns —
/// including the string-converted scope — round-trip a Store snapshot with its source link intact.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class DefinitionScopeSchemaFlowTests
{
    private readonly PostgresFixture _fixture;

    public DefinitionScopeSchemaFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_created_agent_and_skill_default_to_Working_scope_with_no_source_link()
    {
        var (teamId, userId) = await SeedTeamAsync();

        Guid agentId;
        Guid skillId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            agentId = await scope.Resolve<IMediator>().Send(new CreateAgentDefinitionCommand { Name = "Backend Architect" });
        using (var scope = _fixture.BeginScope())
            skillId = await scope.Resolve<ISkillDefinitionService>().CreateAsync(teamId, new SkillDefinitionInput { Name = "TDD", Body = "x" }, userId, default);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var agent = await db.AgentDefinition.AsNoTracking().SingleAsync(a => a.Id == agentId);
        agent.Scope.ShouldBe(DefinitionScope.Working, "an agent created on the bench must default to Working — a forgotten stamp must never hide it as a Store snapshot");
        agent.SourceDefinitionId.ShouldBeNull();
        agent.SourceVersion.ShouldBeNull();
        agent.ContentVersion.ShouldBeNull();

        var skill = await db.SkillDefinition.AsNoTracking().SingleAsync(s => s.Id == skillId);
        skill.Scope.ShouldBe(DefinitionScope.Working);
        skill.SourceDefinitionId.ShouldBeNull();
    }

    [Fact]
    public async Task Store_snapshot_columns_round_trip_through_the_string_conversion()
    {
        var (teamId, userId) = await SeedTeamAsync();
        var snapshotId = Guid.NewGuid();
        var copyId = Guid.NewGuid();

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var now = DateTimeOffset.UtcNow;

            db.AgentDefinition.Add(new AgentDefinition
            {
                Id = snapshotId, TeamId = teamId, Slug = "snap-architect", Name = "Snapshot Architect", SystemPrompt = "p",
                Origin = AgentDefinitionOrigin.Imported, Scope = DefinitionScope.Store, PackId = Guid.NewGuid(), SourcePath = "agents/architect.md",
                ContentVersion = "hash-v1",
                CreatedBy = userId, LastModifiedBy = userId, CreatedDate = now, LastModifiedDate = now,
            });
            // A from-store working copy: links back to the snapshot + the version it was copied at. (A distinct
            // slug here — the team-slug re-scope that lets a snapshot + its copy SHARE a slug is a later slice.)
            db.AgentDefinition.Add(new AgentDefinition
            {
                Id = copyId, TeamId = teamId, Slug = "snap-architect-copy", Name = "Snapshot Architect", SystemPrompt = "p",
                Origin = AgentDefinitionOrigin.Imported, Scope = DefinitionScope.Working, SourceDefinitionId = snapshotId, SourceVersion = "hash-v1",
                CreatedBy = userId, LastModifiedBy = userId, CreatedDate = now, LastModifiedDate = now,
            });
            await db.SaveChangesAsync();
        }

        using var verify = _fixture.BeginScope();
        var db2 = verify.Resolve<CodeSpaceDbContext>();

        var snap = await db2.AgentDefinition.AsNoTracking().SingleAsync(a => a.Id == snapshotId);
        snap.Scope.ShouldBe(DefinitionScope.Store, "the Store scope must round-trip through its string column");
        snap.ContentVersion.ShouldBe("hash-v1");

        var copy = await db2.AgentDefinition.AsNoTracking().SingleAsync(a => a.Id == copyId);
        copy.Scope.ShouldBe(DefinitionScope.Working);
        copy.SourceDefinitionId.ShouldBe(snapshotId, "the copy must remember the snapshot it was instantiated from");
        copy.SourceVersion.ShouldBe("hash-v1");
    }

    private async Task<(Guid TeamId, Guid UserId)> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        db.User.Add(new User { Id = userId, Email = $"scope-{userId:N}@test.local", Name = $"scope-user-{userId:N}", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Team.Add(new Team { Id = teamId, Slug = $"scope-team-{teamId:N}", Name = "Scope Test Team", Kind = TeamKind.Workspace, OwnerUserId = userId, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        await db.SaveChangesAsync();
        return (teamId, userId);
    }
}
