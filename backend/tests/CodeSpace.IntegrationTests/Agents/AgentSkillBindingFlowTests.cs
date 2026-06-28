using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Skills;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Agents;
using MediatR;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// The in-app skill-binding vertical end-to-end through the mediator + DB — the editor's "set the skills on a
/// persona" path (<c>PUT /api/agents/{id}/skills</c>) and the picker's skill list (<c>GET /api/skills</c>).
/// Proves the command/query reach the binding service with the team + actor threaded from the request context
/// (not the body), full-replace semantics, that the bound set surfaces on the persona's read, and that a
/// foreign team can bind neither another team's agent nor another team's skill.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AgentSkillBindingFlowTests
{
    private readonly PostgresFixture _fixture;

    public AgentSkillBindingFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Set_binds_skills_through_the_mediator_and_they_surface_on_the_persona()
    {
        var (teamId, userId) = await SeedTeamAsync();
        var agentId = await CreateAgentAsync(teamId, userId, "Backend Dev");
        var tdd = await CreateSkillAsync(teamId, userId, "TDD");
        var debugging = await CreateSkillAsync(teamId, userId, "Debugging");

        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            await scope.Resolve<IMediator>().Send(new SetAgentSkillsCommand { AgentDefinitionId = agentId, SkillDefinitionIds = new[] { tdd, debugging } });
        }

        using var verify = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = verify.Resolve<IMediator>();

        // The bound skills must surface on the persona read the editor reloads after saving.
        var persona = await mediator.Send(new GetAgentDefinitionQuery { AgentDefinitionId = agentId });
        persona!.BoundSkills.Select(s => s.Slug).OrderBy(x => x).ShouldBe(new[] { "debugging", "tdd" });

        // The picker list returns the team's skills.
        var listed = await mediator.Send(new ListSkillsQuery());
        listed.Select(s => s.Slug).OrderBy(x => x).ShouldBe(new[] { "debugging", "tdd" });
    }

    [Fact]
    public async Task Set_is_full_replace_through_the_mediator()
    {
        var (teamId, userId) = await SeedTeamAsync();
        var agentId = await CreateAgentAsync(teamId, userId, "Dev");
        var s1 = await CreateSkillAsync(teamId, userId, "One");
        var s2 = await CreateSkillAsync(teamId, userId, "Two");
        var s3 = await CreateSkillAsync(teamId, userId, "Three");

        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            var m = scope.Resolve<IMediator>();
            await m.Send(new SetAgentSkillsCommand { AgentDefinitionId = agentId, SkillDefinitionIds = new[] { s1, s2 } });
            await m.Send(new SetAgentSkillsCommand { AgentDefinitionId = agentId, SkillDefinitionIds = new[] { s2, s3 } });   // s1 dropped, s3 added, s2 kept
        }

        using var verify = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        // A set is a full replace — absent ids are unbound, new ids bound.
        var persona = await verify.Resolve<IMediator>().Send(new GetAgentDefinitionQuery { AgentDefinitionId = agentId });
        persona!.BoundSkills.Select(s => s.Slug).OrderBy(x => x).ShouldBe(new[] { "three", "two" });
    }

    [Fact]
    public async Task An_empty_set_clears_all_bindings()
    {
        var (teamId, userId) = await SeedTeamAsync();
        var agentId = await CreateAgentAsync(teamId, userId, "Dev");
        var s1 = await CreateSkillAsync(teamId, userId, "Alpha");

        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            var m = scope.Resolve<IMediator>();
            await m.Send(new SetAgentSkillsCommand { AgentDefinitionId = agentId, SkillDefinitionIds = new[] { s1 } });
            await m.Send(new SetAgentSkillsCommand { AgentDefinitionId = agentId, SkillDefinitionIds = Array.Empty<Guid>() });
        }

        using var verify = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var persona = await verify.Resolve<IMediator>().Send(new GetAgentDefinitionQuery { AgentDefinitionId = agentId });
        persona!.BoundSkills.ShouldBeEmpty("an empty set must unbind every skill");
    }

    [Fact]
    public async Task A_foreign_team_can_bind_neither_another_teams_agent_nor_another_teams_skill()
    {
        var (teamA, userA) = await SeedTeamAsync();
        var (teamB, userB) = await SeedTeamAsync();

        var agentA = await CreateAgentAsync(teamA, userA, "A's Agent");
        var skillA = await CreateSkillAsync(teamA, userA, "A Skill");
        var agentB = await CreateAgentAsync(teamB, userB, "B's Agent");

        using var attacker = _fixture.BeginScopeAs(userB, teamB, Roles.Admin);
        var mediator = attacker.Resolve<IMediator>();

        // Bind to A's agent from team B → the agent isn't in B → not-found (no cross-team write).
        await Should.ThrowAsync<KeyNotFoundException>(() => mediator.Send(new SetAgentSkillsCommand { AgentDefinitionId = agentA, SkillDefinitionIds = Array.Empty<Guid>() }),
            customMessage: "binding another team's agent must throw not-found, never mutate it");

        // Bind A's skill onto B's own agent → the skill isn't in B → not-found (no cross-team skill leak).
        await Should.ThrowAsync<KeyNotFoundException>(() => mediator.Send(new SetAgentSkillsCommand { AgentDefinitionId = agentB, SkillDefinitionIds = new[] { skillA } }),
            customMessage: "binding another team's skill must throw not-found");

        // The picker list never leaks A's skills to B.
        (await mediator.Send(new ListSkillsQuery())).ShouldBeEmpty("team B has no skills of its own; A's must not appear");
    }

    private async Task<Guid> CreateAgentAsync(Guid teamId, Guid userId, string name)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateAgentDefinitionCommand { Name = name });
    }

    private async Task<Guid> CreateSkillAsync(Guid teamId, Guid userId, string name)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<ISkillDefinitionService>().CreateAsync(teamId, new SkillDefinitionInput { Name = name, Body = "x" }, userId, default);
    }

    private async Task<(Guid TeamId, Guid UserId)> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        db.User.Add(new User { Id = userId, Email = $"skill-{userId:N}@test.local", Name = $"skill-user-{userId:N}", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Team.Add(new Team { Id = teamId, Slug = $"skill-team-{teamId:N}", Name = "Skill Test Team", Kind = TeamKind.Workspace, OwnerUserId = userId, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        await db.SaveChangesAsync();
        return (teamId, userId);
    }
}
