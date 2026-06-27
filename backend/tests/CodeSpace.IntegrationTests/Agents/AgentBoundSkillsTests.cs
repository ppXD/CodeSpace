using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// The agent read model surfaces its bound skills (the relational replacement for the dropped skills_jsonb blob):
/// <see cref="IAgentDefinitionService.ListAsync"/> / <see cref="IAgentDefinitionService.GetAsync"/> project each
/// persona's <c>BoundSkills</c> from the AgentSkillBinding join through to ACTIVE skills, ordered by handle, with
/// no bindings → empty and a soft-deleted skill excluded.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AgentBoundSkillsTests
{
    private readonly PostgresFixture _fixture;

    public AgentBoundSkillsTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task List_and_Get_project_bound_skills_ordered_active_only()
    {
        var (teamId, userId) = await SeedTeamAsync();

        var reviewer = await SeedAgentAsync(teamId, userId, "reviewer");
        var architect = await SeedAgentAsync(teamId, userId, "architect");   // a SECOND skill-bearing agent
        var writer = await SeedAgentAsync(teamId, userId, "writer");         // no bindings

        var tdd = await SeedSkillAsync(teamId, userId, "tdd", deleted: false);
        var debugging = await SeedSkillAsync(teamId, userId, "systematic-debugging", deleted: false);
        var apiDesign = await SeedSkillAsync(teamId, userId, "api-design", deleted: false);
        var gone = await SeedSkillAsync(teamId, userId, "deprecated-skill", deleted: true);

        await BindAsync(reviewer, tdd, userId);
        await BindAsync(reviewer, debugging, userId);
        await BindAsync(reviewer, gone, userId);        // bound, but the skill is soft-deleted → must not surface
        await BindAsync(architect, apiDesign, userId);

        // Cross-team bait: a skill in ANOTHER team, bound to this team's agent out-of-band (bypassing the
        // same-team-enforcing service). The read must scope the skill side to teamId and exclude it.
        var (otherTeamId, otherUserId) = await SeedTeamAsync();
        var foreign = await SeedSkillAsync(otherTeamId, otherUserId, "foreign-skill", deleted: false);
        await BindAsync(reviewer, foreign, userId);

        using var scope = _fixture.BeginScope();
        var service = scope.Resolve<IAgentDefinitionService>();

        var list = await service.ListAsync(teamId, CancellationToken.None);

        // Each agent gets exactly its OWN skills (the batched GroupBy partition), ordered by handle, active +
        // same-team only — the soft-deleted and the cross-team skills are both excluded.
        var reviewerRow = list.Single(a => a.Slug == "reviewer");
        reviewerRow.BoundSkills.Select(s => s.Slug).ShouldBe(new[] { "systematic-debugging", "tdd" });
        reviewerRow.BoundSkills.Single(s => s.Slug == "tdd").SkillDefinitionId.ShouldBe(tdd);

        list.Single(a => a.Slug == "architect").BoundSkills.Select(s => s.Slug).ShouldBe(new[] { "api-design" }, "the partition gives each agent its own skills, not the other's");
        list.Single(a => a.Slug == "writer").BoundSkills.ShouldBeEmpty("an agent with no bindings carries an empty list");

        // Get projects the same bound skills as the list.
        var fetched = await service.GetAsync(teamId, reviewer, CancellationToken.None);
        fetched!.BoundSkills.Select(s => s.Slug).ShouldBe(new[] { "systematic-debugging", "tdd" });
    }

    private async Task BindAsync(Guid agentId, Guid skillId, Guid userId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.AgentSkillBinding.Add(new AgentSkillBinding { Id = Guid.NewGuid(), AgentDefinitionId = agentId, SkillDefinitionId = skillId, CreatedDate = DateTimeOffset.UtcNow, CreatedBy = userId });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedAgentAsync(Guid teamId, Guid userId, string slug)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var id = Guid.NewGuid();
        db.AgentDefinition.Add(new AgentDefinition { Id = id, TeamId = teamId, Slug = slug, Name = slug, Origin = AgentDefinitionOrigin.Authored, CreatedDate = DateTimeOffset.UtcNow, CreatedBy = userId, LastModifiedDate = DateTimeOffset.UtcNow, LastModifiedBy = userId });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedSkillAsync(Guid teamId, Guid userId, string slug, bool deleted)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var id = Guid.NewGuid();
        db.SkillDefinition.Add(new SkillDefinition { Id = id, TeamId = teamId, Slug = slug, Name = slug, Origin = SkillDefinitionOrigin.Authored, DeletedDate = deleted ? DateTimeOffset.UtcNow : null, CreatedDate = DateTimeOffset.UtcNow, CreatedBy = userId, LastModifiedDate = DateTimeOffset.UtcNow, LastModifiedBy = userId });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<(Guid TeamId, Guid UserId)> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"bound-{userId:N}@test.local", Name = $"bound-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"bound-{teamId:N}", Name = "Bound Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return (teamId, userId);
    }
}
