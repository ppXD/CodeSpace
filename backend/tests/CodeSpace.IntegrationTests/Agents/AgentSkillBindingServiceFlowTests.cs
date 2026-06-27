using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Skills;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// The agent↔skill binding vertical through <see cref="IAgentSkillBindingService"/> + real Postgres. Proves
/// the replace-set semantics (add/remove diff), both lookup directions (skills-of-agent, agents-of-skill),
/// idempotent no-op saves, tenancy on both sides, and that a soft-deleted skill silently drops out of the
/// agent's list (reads join through to active definitions) without deleting binding rows.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AgentSkillBindingServiceFlowTests
{
    private readonly PostgresFixture _fixture;

    public AgentSkillBindingServiceFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Set_binds_skills_and_ListForAgent_returns_them()
    {
        var (teamId, userId) = await SeedTeamAsync();
        var agent = await CreateAgentAsync(teamId, userId, "Backend Dev");
        var s1 = await CreateSkillAsync(teamId, userId, "TDD");
        var s2 = await CreateSkillAsync(teamId, userId, "Debugging");

        await SetAsync(teamId, agent, new[] { s1, s2 }, userId);

        using var scope = _fixture.BeginScope();
        var skills = await scope.Resolve<IAgentSkillBindingService>().ListForAgentAsync(teamId, agent, default);

        skills.Select(s => s.Slug).OrderBy(x => x).ShouldBe(new[] { "debugging", "tdd" });
    }

    [Fact]
    public async Task Set_replaces_the_whole_set()
    {
        var (teamId, userId) = await SeedTeamAsync();
        var agent = await CreateAgentAsync(teamId, userId, "Dev");
        var s1 = await CreateSkillAsync(teamId, userId, "One");
        var s2 = await CreateSkillAsync(teamId, userId, "Two");
        var s3 = await CreateSkillAsync(teamId, userId, "Three");

        await SetAsync(teamId, agent, new[] { s1, s2 }, userId);
        await SetAsync(teamId, agent, new[] { s2, s3 }, userId);   // s1 dropped, s3 added, s2 kept

        using var scope = _fixture.BeginScope();
        var skills = await scope.Resolve<IAgentSkillBindingService>().ListForAgentAsync(teamId, agent, default);

        skills.Select(s => s.Slug).OrderBy(x => x).ShouldBe(new[] { "three", "two" });
    }

    [Fact]
    public async Task Set_is_idempotent()
    {
        var (teamId, userId) = await SeedTeamAsync();
        var agent = await CreateAgentAsync(teamId, userId, "Dev");
        var s1 = await CreateSkillAsync(teamId, userId, "Solo");

        await SetAsync(teamId, agent, new[] { s1 }, userId);
        await SetAsync(teamId, agent, new[] { s1 }, userId);   // no-op: same set

        using var scope = _fixture.BeginScope();
        var skills = await scope.Resolve<IAgentSkillBindingService>().ListForAgentAsync(teamId, agent, default);
        skills.Count.ShouldBe(1);   // not double-bound
    }

    [Fact]
    public async Task ListAgentsForSkill_is_the_reverse_lookup()
    {
        var (teamId, userId) = await SeedTeamAsync();
        var agentA = await CreateAgentAsync(teamId, userId, "Agent A");
        var agentB = await CreateAgentAsync(teamId, userId, "Agent B");
        var s1 = await CreateSkillAsync(teamId, userId, "Shared");

        await SetAsync(teamId, agentA, new[] { s1 }, userId);
        await SetAsync(teamId, agentB, new[] { s1 }, userId);

        using var scope = _fixture.BeginScope();
        var agents = await scope.Resolve<IAgentSkillBindingService>().ListAgentsForSkillAsync(teamId, s1, default);

        agents.Select(a => a.Slug).OrderBy(x => x).ShouldBe(new[] { "agent-a", "agent-b" });
    }

    [Fact]
    public async Task Set_rejects_a_skill_from_another_team()
    {
        var (teamA, userA) = await SeedTeamAsync();
        var (teamB, userB) = await SeedTeamAsync();
        var agent = await CreateAgentAsync(teamA, userA, "Dev");
        var foreignSkill = await CreateSkillAsync(teamB, userB, "Foreign");

        await Should.ThrowAsync<KeyNotFoundException>(() => SetAsync(teamA, agent, new[] { foreignSkill }, userA));
    }

    [Fact]
    public async Task Set_rejects_an_agent_from_another_team()
    {
        var (teamA, userA) = await SeedTeamAsync();
        var (teamB, userB) = await SeedTeamAsync();
        var foreignAgent = await CreateAgentAsync(teamB, userB, "Foreign");
        var skill = await CreateSkillAsync(teamA, userA, "Mine");

        await Should.ThrowAsync<KeyNotFoundException>(() => SetAsync(teamA, foreignAgent, new[] { skill }, userA));
    }

    [Fact]
    public async Task A_soft_deleted_skill_drops_out_of_the_agents_list()
    {
        var (teamId, userId) = await SeedTeamAsync();
        var agent = await CreateAgentAsync(teamId, userId, "Dev");
        var s1 = await CreateSkillAsync(teamId, userId, "Doomed");

        await SetAsync(teamId, agent, new[] { s1 }, userId);

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<ISkillDefinitionService>().DeleteAsync(teamId, s1, userId, default);

        using var verify = _fixture.BeginScope();
        var skills = await verify.Resolve<IAgentSkillBindingService>().ListForAgentAsync(teamId, agent, default);
        skills.ShouldBeEmpty(customMessage: "the list reads join through to ACTIVE skills, so a soft-deleted skill drops out");

        // …and the binding ROW physically survives (it's hidden by the active-skill join, not deleted), so the
        // skill reappears if undeleted — the expand-contract importer relies on bindings outliving a soft-delete.
        (await CountBindingsAsync(agent)).ShouldBe(1, customMessage: "soft-deleting the skill must NOT delete the binding row");
    }

    [Fact]
    public async Task Set_with_an_empty_list_clears_all_bindings()
    {
        var (teamId, userId) = await SeedTeamAsync();
        var agent = await CreateAgentAsync(teamId, userId, "Dev");
        var s1 = await CreateSkillAsync(teamId, userId, "One");
        var s2 = await CreateSkillAsync(teamId, userId, "Two");

        await SetAsync(teamId, agent, new[] { s1, s2 }, userId);
        await SetAsync(teamId, agent, Array.Empty<Guid>(), userId);   // clear all

        using var scope = _fixture.BeginScope();
        (await scope.Resolve<IAgentSkillBindingService>().ListForAgentAsync(teamId, agent, default)).ShouldBeEmpty();
        (await CountBindingsAsync(agent)).ShouldBe(0, customMessage: "an empty set must physically delete every binding row, not no-op");
    }

    [Fact]
    public async Task Set_rejects_a_soft_deleted_skill()
    {
        var (teamId, userId) = await SeedTeamAsync();
        var agent = await CreateAgentAsync(teamId, userId, "Dev");
        var s1 = await CreateSkillAsync(teamId, userId, "Doomed");

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<ISkillDefinitionService>().DeleteAsync(teamId, s1, userId, default);

        // A same-team but soft-deleted skill must be rejected by the active-only validation (distinct from the
        // cross-team rejection — this exercises the DeletedDate==null arm of the tenancy check).
        await Should.ThrowAsync<KeyNotFoundException>(() => SetAsync(teamId, agent, new[] { s1 }, userId));
    }

    private async Task<int> CountBindingsAsync(Guid agentId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().AgentSkillBinding.CountAsync(b => b.AgentDefinitionId == agentId);
    }

    private async Task SetAsync(Guid teamId, Guid agentId, IReadOnlyList<Guid> skillIds, Guid userId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IAgentSkillBindingService>().SetForAgentAsync(teamId, agentId, skillIds, userId, default);
    }

    private async Task<Guid> CreateAgentAsync(Guid teamId, Guid userId, string name)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IAgentDefinitionService>().CreateAsync(teamId, new AgentDefinitionInput { Name = name }, userId, default);
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
        db.User.Add(new User { Id = userId, Email = $"bind-{userId:N}@test.local", Name = $"bind-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"bind-{teamId:N}", Name = "Bind Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return (teamId, userId);
    }
}
