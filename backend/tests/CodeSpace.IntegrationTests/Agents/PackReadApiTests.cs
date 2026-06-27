using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Agents;
using MediatR;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// The Library/store read model (<see cref="IPackService"/>): ListAsync returns the team's packs ordered by
/// name with ACTIVE agent + skill counts and freshness; GetAsync returns a pack's artifacts (agents + skills,
/// active only) or null for an unknown / cross-team pack. Tenant-scoped throughout.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class PackReadApiTests
{
    private readonly PostgresFixture _fixture;

    public PackReadApiTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task List_returns_packs_ordered_with_active_counts_and_freshness()
    {
        var (teamId, userId) = await SeedTeamAsync();

        var synced = DateTimeOffset.UtcNow.AddHours(-2);
        var filled = await SeedPackAsync(teamId, userId, "z-filled", PackKind.Github, "https://github.com/obra/superpowers", "v6.0.3", "abc123", synced);
        var empty = await SeedPackAsync(teamId, userId, "a-empty", PackKind.GitUrl, "https://git.example.com/x", null, null, null);

        await SeedAgentAsync(teamId, userId, "backend-architect", filled, deleted: false);
        await SeedAgentAsync(teamId, userId, "code-reviewer", filled, deleted: false);
        await SeedSkillAsync(teamId, userId, "tdd", filled, deleted: false);
        await SeedSkillAsync(teamId, userId, "gone", filled, deleted: true);   // soft-deleted → must not count
        await SeedAgentAsync(teamId, userId, "authored-one", packId: null, deleted: false);   // no pack → counts toward nothing

        // A different team's pack must never appear.
        var (otherTeam, otherUser) = await SeedTeamAsync();
        await SeedPackAsync(otherTeam, otherUser, "foreign", PackKind.Github, "https://github.com/x/y", null, null, null);

        using var scope = _fixture.BeginScope();
        var list = await scope.Resolve<IPackService>().ListAsync(teamId, CancellationToken.None);

        list.Select(p => p.Name).ShouldBe(new[] { "a-empty", "z-filled" }, "ordered by name, the foreign team's pack excluded");

        var filledRow = list.Single(p => p.Id == filled);
        filledRow.AgentCount.ShouldBe(2);
        filledRow.SkillCount.ShouldBe(1, "the soft-deleted skill is excluded");
        filledRow.Kind.ShouldBe(PackKind.Github);
        filledRow.Reference.ShouldBe("v6.0.3");
        filledRow.LastSyncedSha.ShouldBe("abc123");
        filledRow.LastSyncedDate!.Value.ShouldBe(synced, TimeSpan.FromSeconds(1));

        var emptyRow = list.Single(p => p.Id == empty);
        emptyRow.AgentCount.ShouldBe(0);
        emptyRow.SkillCount.ShouldBe(0);
    }

    [Fact]
    public async Task Get_returns_artifacts_active_only_and_null_for_unknown_or_cross_team()
    {
        var (teamId, userId) = await SeedTeamAsync();
        var pack = await SeedPackAsync(teamId, userId, "obra/superpowers", PackKind.Github, "https://github.com/obra/superpowers", "v6.0.3", null, null);

        // Interleaving slugs across kinds prove the ordering: agents BEFORE skills, ordinal slug tiebreak within a kind.
        await SeedAgentAsync(teamId, userId, "zebra", pack, deleted: false);
        await SeedAgentAsync(teamId, userId, "alpha", pack, deleted: false);
        await SeedSkillAsync(teamId, userId, "yak", pack, deleted: false);
        await SeedSkillAsync(teamId, userId, "beta", pack, deleted: false);
        await SeedSkillAsync(teamId, userId, "gone", pack, deleted: true);

        using var scope = _fixture.BeginScope();
        var service = scope.Resolve<IPackService>();

        var detail = await service.GetAsync(teamId, pack, CancellationToken.None);
        detail.ShouldNotBeNull();
        detail!.Pack.AgentCount.ShouldBe(2);
        detail.Pack.SkillCount.ShouldBe(2, "the soft-deleted skill is excluded");

        // Agents before skills, ordinal slug tiebreak within each kind; soft-deleted excluded.
        detail.Artifacts.Select(a => (a.Kind, a.Slug)).ShouldBe(new[]
        {
            (PackArtifactKind.Agent, "alpha"),
            (PackArtifactKind.Agent, "zebra"),
            (PackArtifactKind.Skill, "beta"),
            (PackArtifactKind.Skill, "yak"),
        });
        detail.Artifacts.Single(a => a.Slug == "alpha").SourcePath.ShouldBe("agents/alpha.md");

        (await service.GetAsync(teamId, Guid.NewGuid(), CancellationToken.None)).ShouldBeNull("an unknown pack is null");

        var (otherTeam, _) = await SeedTeamAsync();
        (await service.GetAsync(otherTeam, pack, CancellationToken.None)).ShouldBeNull("a pack in another team is null (tenancy)");
    }

    [Fact]
    public async Task Mediator_threads_the_current_team_so_packs_are_invisible_to_another_team()
    {
        var (teamA, userA) = await SeedTeamAsync();
        var (teamB, userB) = await SeedTeamAsync();

        var packInA = await SeedPackAsync(teamA, userA, "obra/superpowers", PackKind.Github, "https://github.com/obra/superpowers", "v6.0.3", null, null);
        await SeedSkillAsync(teamA, userA, "tdd", packInA, deleted: false);

        // Owner of A: the handler threads ICurrentTeam → the pack lists + resolves.
        using (var owner = _fixture.BeginScopeAs(userA, teamA, Roles.Admin))
        {
            var mediator = owner.Resolve<IMediator>();
            (await mediator.Send(new ListPacksQuery())).Select(p => p.Id).ShouldContain(packInA);
            (await mediator.Send(new GetPackQuery { PackId = packInA }))!.Pack.Name.ShouldBe("obra/superpowers");
        }

        // A member of team B presenting A's pack id: the handler uses B's ICurrentTeam (never the payload), so
        // A's pack is invisible — Get is null and List omits it. This is the cross-tenant guarantee.
        using var attacker = _fixture.BeginScopeAs(userB, teamB, Roles.Admin);
        var b = attacker.Resolve<IMediator>();
        (await b.Send(new GetPackQuery { PackId = packInA })).ShouldBeNull("a foreign team's pack MUST resolve null, never confirm existence");
        (await b.Send(new ListPacksQuery())).Select(p => p.Id).ShouldNotContain(packInA);
    }

    private async Task<Guid> SeedPackAsync(Guid teamId, Guid userId, string name, PackKind kind, string? url, string? reference, string? sha, DateTimeOffset? synced)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var id = Guid.NewGuid();
        db.Pack.Add(new Pack { Id = id, TeamId = teamId, Kind = kind, Name = name, Url = url, Reference = reference, LastSyncedSha = sha, LastSyncedDate = synced, CreatedDate = DateTimeOffset.UtcNow, CreatedBy = userId, LastModifiedDate = DateTimeOffset.UtcNow, LastModifiedBy = userId });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task SeedAgentAsync(Guid teamId, Guid userId, string slug, Guid? packId, bool deleted)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.AgentDefinition.Add(new AgentDefinition { Id = Guid.NewGuid(), TeamId = teamId, Slug = slug, Name = slug, Origin = packId == null ? AgentDefinitionOrigin.Authored : AgentDefinitionOrigin.Imported, PackId = packId, SourcePath = packId == null ? null : $"agents/{slug}.md", DeletedDate = deleted ? DateTimeOffset.UtcNow : null, CreatedDate = DateTimeOffset.UtcNow, CreatedBy = userId, LastModifiedDate = DateTimeOffset.UtcNow, LastModifiedBy = userId });
        await db.SaveChangesAsync();
    }

    private async Task SeedSkillAsync(Guid teamId, Guid userId, string slug, Guid? packId, bool deleted)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.SkillDefinition.Add(new SkillDefinition { Id = Guid.NewGuid(), TeamId = teamId, Slug = slug, Name = slug, Origin = packId == null ? SkillDefinitionOrigin.Authored : SkillDefinitionOrigin.Imported, PackId = packId, SourcePath = packId == null ? null : $"skills/{slug}/SKILL.md", DeletedDate = deleted ? DateTimeOffset.UtcNow : null, CreatedDate = DateTimeOffset.UtcNow, CreatedBy = userId, LastModifiedDate = DateTimeOffset.UtcNow, LastModifiedBy = userId });
        await db.SaveChangesAsync();
    }

    private async Task<(Guid TeamId, Guid UserId)> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"packs-{userId:N}@test.local", Name = $"packs-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"packs-{teamId:N}", Name = "Packs Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return (teamId, userId);
    }
}
