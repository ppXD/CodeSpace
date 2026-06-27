using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Enums;
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

        await SeedAgentAsync(teamId, userId, "backend-architect", pack, deleted: false);
        await SeedSkillAsync(teamId, userId, "tdd", pack, deleted: false);
        await SeedSkillAsync(teamId, userId, "gone", pack, deleted: true);

        using var scope = _fixture.BeginScope();
        var service = scope.Resolve<IPackService>();

        var detail = await service.GetAsync(teamId, pack, CancellationToken.None);
        detail.ShouldNotBeNull();
        detail!.Pack.AgentCount.ShouldBe(1);
        detail.Pack.SkillCount.ShouldBe(1);

        // Agents before skills, soft-deleted excluded; the artifact carries its source path.
        detail.Artifacts.Select(a => (a.Kind, a.Slug)).ShouldBe(new[] { (PackArtifactKindFor("agent"), "backend-architect"), (PackArtifactKindFor("skill"), "tdd") });
        detail.Artifacts.Single(a => a.Slug == "backend-architect").SourcePath.ShouldBe("agents/backend-architect.md");

        (await service.GetAsync(teamId, Guid.NewGuid(), CancellationToken.None)).ShouldBeNull("an unknown pack is null");

        var (otherTeam, _) = await SeedTeamAsync();
        (await service.GetAsync(otherTeam, pack, CancellationToken.None)).ShouldBeNull("a pack in another team is null (tenancy)");
    }

    private static PackArtifactKind PackArtifactKindFor(string kind) => kind == "agent" ? PackArtifactKind.Agent : PackArtifactKind.Skill;

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
