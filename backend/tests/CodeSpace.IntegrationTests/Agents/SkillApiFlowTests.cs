using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Skills;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Agents;
using CodeSpace.Messages.Commands.Agents;
using MediatR;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// The skill detail + delete API vertical through the mediator + DB — the Library detail modal's read
/// (<c>GET /api/skills/{id}</c>, Level-2 with the SKILL.md body) and its delete (<c>DELETE /api/skills/{id}</c>).
/// Proves the body surfaces, a soft-delete drops the skill from both the detail read and the list, and a foreign
/// team can neither read another team's skill nor delete it.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class SkillApiFlowTests
{
    private readonly PostgresFixture _fixture;

    public SkillApiFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Get_returns_the_skill_detail_with_its_body()
    {
        var (teamId, userId) = await SeedTeamAsync();
        var skillId = await CreateSkillAsync(teamId, userId, "Test Driven Development", "Write the failing test first.");

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var detail = await scope.Resolve<IMediator>().Send(new GetSkillQuery { SkillDefinitionId = skillId });

        detail.ShouldNotBeNull();
        detail!.Slug.ShouldBe("test-driven-development");
        detail.Body.ShouldBe("Write the failing test first.", customMessage: "the detail read must carry the SKILL.md body the modal shows");
    }

    [Fact]
    public async Task Delete_soft_deletes_so_the_skill_drops_from_the_detail_read_and_the_list()
    {
        var (teamId, userId) = await SeedTeamAsync();
        var skillId = await CreateSkillAsync(teamId, userId, "Doomed", "x");

        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            await scope.Resolve<IMediator>().Send(new DeleteSkillCommand { SkillDefinitionId = skillId });
        }

        using var verify = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = verify.Resolve<IMediator>();

        (await mediator.Send(new GetSkillQuery { SkillDefinitionId = skillId })).ShouldBeNull("a soft-deleted skill must not come back from the detail read");
        (await mediator.Send(new ListSkillsQuery())).ShouldBeEmpty("a soft-deleted skill must drop out of the team's skill list");
    }

    [Fact]
    public async Task A_foreign_team_can_neither_read_nor_delete_another_teams_skill()
    {
        var (teamA, userA) = await SeedTeamAsync();
        var (teamB, userB) = await SeedTeamAsync();
        var skillA = await CreateSkillAsync(teamA, userA, "A Secret Skill", "secret");

        using var attacker = _fixture.BeginScopeAs(userB, teamB, Roles.Admin);
        var mediator = attacker.Resolve<IMediator>();

        (await mediator.Send(new GetSkillQuery { SkillDefinitionId = skillA }))
            .ShouldBeNull("a foreign team's Get MUST return null — returning the skill is a cross-team leak");

        await Should.ThrowAsync<KeyNotFoundException>(() => mediator.Send(new DeleteSkillCommand { SkillDefinitionId = skillA }),
            customMessage: "a foreign team's Delete MUST throw not-found, never remove another tenant's skill");

        // Ground truth: A's skill is untouched.
        using var verify = _fixture.BeginScopeAs(userA, teamA, Roles.Admin);
        (await verify.Resolve<IMediator>().Send(new GetSkillQuery { SkillDefinitionId = skillA })).ShouldNotBeNull("B's delete threw, so A's skill must remain");
    }

    private async Task<Guid> CreateSkillAsync(Guid teamId, Guid userId, string name, string body)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<ISkillDefinitionService>().CreateAsync(teamId, new SkillDefinitionInput { Name = name, Body = body }, userId, default);
    }

    private async Task<(Guid TeamId, Guid UserId)> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        db.User.Add(new User { Id = userId, Email = $"skillapi-{userId:N}@test.local", Name = $"skillapi-user-{userId:N}", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Team.Add(new Team { Id = teamId, Slug = $"skillapi-team-{teamId:N}", Name = "Skill API Test Team", Kind = TeamKind.Workspace, OwnerUserId = userId, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        await db.SaveChangesAsync();
        return (teamId, userId);
    }
}
