using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Agents;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// The Library/store read model (<see cref="IPackService"/>): ListAsync returns the team's packs that own at
/// least one STORE snapshot, ordered by name with ACTIVE store agent + skill counts and freshness (a pack whose
/// artifacts are all grandfathered Working rows is hidden); GetAsync returns a pack's STORE artifacts (agents +
/// skills, active only) or null for an unknown / cross-team pack. Tenant-scoped throughout.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class PackReadApiTests
{
    private readonly PostgresFixture _fixture;

    public PackReadApiTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task List_returns_store_packs_ordered_with_active_counts_hiding_grandfathered_packs()
    {
        var (teamId, userId) = await SeedTeamAsync();

        var synced = DateTimeOffset.UtcNow.AddHours(-2);
        var filled = await SeedPackAsync(teamId, userId, "z-filled", PackKind.Github, "https://github.com/obra/superpowers", "v6.0.3", "abc123", synced);
        var grandfathered = await SeedPackAsync(teamId, userId, "a-grandfathered", PackKind.GitUrl, "https://git.example.com/x", null, null, null);

        // z-filled owns STORE snapshots — the Library shape.
        await SeedAgentAsync(teamId, userId, "backend-architect", filled, deleted: false);
        await SeedAgentAsync(teamId, userId, "code-reviewer", filled, deleted: false);
        await SeedSkillAsync(teamId, userId, "tdd", filled, deleted: false);
        await SeedSkillAsync(teamId, userId, "gone", filled, deleted: true);   // soft-deleted → must not count

        // The grandfathered pack's artifacts are all WORKING (imported before the store model) → the Library hides it.
        await SeedAgentAsync(teamId, userId, "legacy-agent", grandfathered, deleted: false, scope: DefinitionScope.Working);
        await SeedSkillAsync(teamId, userId, "legacy-skill", grandfathered, deleted: false, scope: DefinitionScope.Working);

        await SeedAgentAsync(teamId, userId, "authored-one", packId: null, deleted: false, scope: DefinitionScope.Working);   // no pack → counts toward nothing

        // A different team's pack — WITH a store snapshot, so ONLY tenancy (never the empty-filter) can exclude it.
        var (otherTeam, otherUser) = await SeedTeamAsync();
        var foreign = await SeedPackAsync(otherTeam, otherUser, "foreign", PackKind.Github, "https://github.com/x/y", null, null, null);
        await SeedAgentAsync(otherTeam, otherUser, "foreign-agent", foreign, deleted: false);

        using var scope = _fixture.BeginScope();
        var list = await scope.Resolve<IPackService>().ListAsync(teamId, CancellationToken.None);

        list.Select(p => p.Name).ShouldBe(new[] { "z-filled" }, "only packs with a store snapshot show: the grandfathered (all-Working) pack and the foreign team's pack are both absent");
        list.Select(p => p.Id).ShouldNotContain(grandfathered, "a pack whose artifacts are all grandfathered Working rows is hidden until re-imported");

        var filledRow = list.Single(p => p.Id == filled);
        filledRow.AgentCount.ShouldBe(2);
        filledRow.SkillCount.ShouldBe(1, "the soft-deleted skill is excluded");
        filledRow.Kind.ShouldBe(PackKind.Github);
        filledRow.Reference.ShouldBe("v6.0.3");
        filledRow.LastSyncedSha.ShouldBe("abc123");
        filledRow.LastSyncedDate!.Value.ShouldBe(synced, TimeSpan.FromSeconds(1));
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
        await SeedAgentAsync(teamId, userId, "grandfathered-agent", pack, deleted: false, scope: DefinitionScope.Working);   // a Working bench agent in this pack — Get is store-scoped, so it must NOT appear
        await SeedSkillAsync(teamId, userId, "grandfathered-skill", pack, deleted: false, scope: DefinitionScope.Working);   // …and a Working bench skill, guarding the skill-side store filter independently

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

    [Fact]
    public async Task A_team_can_hold_only_one_custom_pack()
    {
        var (teamId, userId) = await SeedTeamAsync();

        await SeedPackAsync(teamId, userId, "Custom", PackKind.Custom, url: null, reference: null, sha: null, synced: null);

        // The uq_pack_team_custom singleton index (migration 0089) — a second url-less Custom pack for the team is
        // rejected, so EnsureCustomPack's find-or-create can never silently fork two Custom packs.
        await Should.ThrowAsync<DbUpdateException>(() => SeedPackAsync(teamId, userId, "Custom", PackKind.Custom, url: null, reference: null, sha: null, synced: null));
    }

    [Fact]
    public async Task ListArtifacts_pages_one_kind_store_scoped_ordered_by_slug()
    {
        var (teamId, userId) = await SeedTeamAsync();
        var pack = await SeedPackAsync(teamId, userId, "obra/superpowers", PackKind.Github, "https://github.com/obra/superpowers", "v6.0.3", null, null);

        // Five active store agents (out-of-order slugs prove the OrderBy), plus rows that the query MUST exclude.
        foreach (var slug in new[] { "echo", "alpha", "delta", "bravo", "charlie" })
            await SeedAgentAsync(teamId, userId, slug, pack, deleted: false);

        await SeedAgentAsync(teamId, userId, "deleted-agent", pack, deleted: true);                                   // soft-deleted → excluded
        await SeedAgentAsync(teamId, userId, "working-agent", pack, deleted: false, scope: DefinitionScope.Working);  // bench (Working) → excluded
        await SeedSkillAsync(teamId, userId, "a-skill", pack, deleted: false);                                        // a Skill in the SAME pack → must not bleed into an Agent page

        using var scope = _fixture.BeginScope();
        var service = scope.Resolve<IPackService>();

        var page0 = await service.ListArtifactsAsync(teamId, pack, PackArtifactKind.Agent, search: null, page: 0, pageSize: 2, CancellationToken.None);
        page0.Total.ShouldBe(5, "five active store agents; the deleted, the Working bench row and the skill are all excluded");
        page0.PageCount.ShouldBe(3, "ceil(5 / 2)");
        page0.Page.ShouldBe(0);
        page0.Items.Select(a => a.Slug).ShouldBe(new[] { "alpha", "bravo" });
        page0.Items.ShouldAllBe(a => a.Kind == PackArtifactKind.Agent);

        var page1 = await service.ListArtifactsAsync(teamId, pack, PackArtifactKind.Agent, search: null, page: 1, pageSize: 2, CancellationToken.None);
        page1.Page.ShouldBe(1, "an in-range non-zero page is echoed back faithfully (not clamped)");
        page1.Items.Select(a => a.Slug).ShouldBe(new[] { "charlie", "delta" });

        var page2 = await service.ListArtifactsAsync(teamId, pack, PackArtifactKind.Agent, search: null, page: 2, pageSize: 2, CancellationToken.None);
        page2.Items.Select(a => a.Slug).ShouldBe(new[] { "echo" }, "the last page holds the remainder");

        // pageSize is clamped to a floor of 1 — a 0/negative size never yields a divide-by-zero or an all-rows page.
        var tiny = await service.ListArtifactsAsync(teamId, pack, PackArtifactKind.Agent, search: null, page: 0, pageSize: 0, CancellationToken.None);
        tiny.PageCount.ShouldBe(5, "pageSize 0 clamps to 1, so five agents span five pages");
        tiny.Items.Select(a => a.Slug).ShouldBe(new[] { "alpha" });

        var skills = await service.ListArtifactsAsync(teamId, pack, PackArtifactKind.Skill, search: null, page: 0, pageSize: 10, CancellationToken.None);
        skills.Total.ShouldBe(1, "the Skill kind is isolated from the agents");
        skills.Items.Single().Slug.ShouldBe("a-skill");
    }

    [Fact]
    public async Task ListArtifacts_paginates_duplicate_slug_rows_stably_by_id_tie_break()
    {
        var (teamId, userId) = await SeedTeamAsync();
        var pack = await SeedPackAsync(teamId, userId, "obra/superpowers", PackKind.Github, "https://github.com/obra/superpowers", "v6.0.3", null, null);

        // Store snapshots carry NO unique handle (uq_*_team_slug is Working-only) — two source files whose names
        // derive the same slug land as DISTINCT Store rows (unique on (pack, source_path), not slug). With only
        // OrderBy(Slug) these tied rows have an undefined order that can differ per query, so Skip/Take across
        // separate page executions could drop or repeat one. The Id tie-break makes the traversal stable.
        for (var i = 0; i < 5; i++)
            await SeedAgentAsync(teamId, userId, "deploy-tool", pack, deleted: false, name: $"Deploy Tool {i}", sourcePath: $"agents/deploy-{i}.md");

        using var scope = _fixture.BeginScope();
        var service = scope.Resolve<IPackService>();

        // One full page: all slugs are equal, so OrderBy(Slug).ThenBy(Id) collapses to pure Id-ascending order.
        // Without the tie-break the rows would come back in heap/insertion order, not sorted by their random Ids.
        var whole = await service.ListArtifactsAsync(teamId, pack, PackArtifactKind.Agent, search: null, page: 0, pageSize: 10, CancellationToken.None);
        whole.Total.ShouldBe(5);
        whole.Items.Select(a => a.Id).ShouldBe(whole.Items.Select(a => a.Id).OrderBy(id => id), "duplicate-slug rows order by the Id tie-break");

        // Paging across THREE separate executions must visit each distinct row exactly once — no drop, no repeat.
        var seen = new List<Guid>();
        for (var p = 0; p < 3; p++)
        {
            var page = await service.ListArtifactsAsync(teamId, pack, PackArtifactKind.Agent, search: null, page: p, pageSize: 2, CancellationToken.None);
            seen.AddRange(page.Items.Select(a => a.Id));
        }

        seen.Distinct().Count().ShouldBe(5, "the Id tie-break keeps the OFFSET/LIMIT traversal stable across the separate per-page queries");
    }

    [Fact]
    public async Task ListArtifacts_filters_by_name_or_handle_case_insensitively()
    {
        var (teamId, userId) = await SeedTeamAsync();
        var pack = await SeedPackAsync(teamId, userId, "obra/superpowers", PackKind.Github, "https://github.com/obra/superpowers", "v6.0.3", null, null);

        await SeedAgentAsync(teamId, userId, "alpha-tool", pack, deleted: false, name: "Zephyr Helper");          // search hits NAME only (slug has no "zephyr")
        await SeedAgentAsync(teamId, userId, "backend-architect", pack, deleted: false, name: "Cloud Wrangler");  // search hits SLUG only (name has no "backend") — isolates the slug operand
        await SeedAgentAsync(teamId, userId, "unrelated", pack, deleted: false);
        await SeedSkillAsync(teamId, userId, "git-rebase", pack, deleted: false, name: "Rebase Helper");          // a Skill, to prove search filters the Skill kind too

        using var scope = _fixture.BeginScope();
        var service = scope.Resolve<IPackService>();

        var byName = await service.ListArtifactsAsync(teamId, pack, PackArtifactKind.Agent, search: "ZEPHYR", page: 0, pageSize: 10, CancellationToken.None);
        byName.Total.ShouldBe(1, "case-insensitive match against the display NAME (the slug has no 'zephyr', so only the Name operand can match)");
        byName.Items.Single().Slug.ShouldBe("alpha-tool");

        var bySlug = await service.ListArtifactsAsync(teamId, pack, PackArtifactKind.Agent, search: "Backend", page: 0, pageSize: 10, CancellationToken.None);
        bySlug.Total.ShouldBe(1, "case-insensitive match against the handle/slug (the Name is 'Cloud Wrangler', so only the Slug operand can match — isolates that branch)");
        bySlug.Items.Single().Slug.ShouldBe("backend-architect");

        var blank = await service.ListArtifactsAsync(teamId, pack, PackArtifactKind.Agent, search: "   ", page: 0, pageSize: 10, CancellationToken.None);
        blank.Total.ShouldBe(3, "a whitespace-only search is treated as no filter (the skill is a different kind)");

        var skillSearch = await service.ListArtifactsAsync(teamId, pack, PackArtifactKind.Skill, search: "REBASE", page: 0, pageSize: 10, CancellationToken.None);
        skillSearch.Total.ShouldBe(1, "search filters the Skill kind by name too, case-insensitively");
        skillSearch.Items.Single().Slug.ShouldBe("git-rebase");
    }

    [Fact]
    public async Task ListArtifacts_clamps_an_out_of_range_page_and_is_tenant_scoped()
    {
        var (teamId, userId) = await SeedTeamAsync();
        var pack = await SeedPackAsync(teamId, userId, "obra/superpowers", PackKind.Github, "https://github.com/obra/superpowers", "v6.0.3", null, null);

        foreach (var slug in new[] { "alpha", "bravo", "charlie" })
            await SeedAgentAsync(teamId, userId, slug, pack, deleted: false);

        using var scope = _fixture.BeginScope();
        var service = scope.Resolve<IPackService>();

        var beyond = await service.ListArtifactsAsync(teamId, pack, PackArtifactKind.Agent, search: null, page: 99, pageSize: 2, CancellationToken.None);
        beyond.PageCount.ShouldBe(2, "ceil(3 / 2)");
        beyond.Page.ShouldBe(1, "a page past the end clamps to the last page rather than returning empty");
        beyond.Items.Select(a => a.Slug).ShouldBe(new[] { "charlie" });

        var negative = await service.ListArtifactsAsync(teamId, pack, PackArtifactKind.Agent, search: null, page: -5, pageSize: 2, CancellationToken.None);
        negative.Page.ShouldBe(0, "a negative page clamps up to the first page");
        negative.Items.Select(a => a.Slug).ShouldBe(new[] { "alpha", "bravo" });

        // An empty pack reports a sane single-page shape, never PageCount 0.
        var empty = await service.ListArtifactsAsync(teamId, pack, PackArtifactKind.Skill, search: null, page: 0, pageSize: 10, CancellationToken.None);
        empty.Total.ShouldBe(0);
        empty.PageCount.ShouldBe(1);
        empty.Items.ShouldBeEmpty();

        // Tenancy: a foreign team presenting this pack's id sees nothing — the artifact teamId filter is the guard.
        var (otherTeam, _) = await SeedTeamAsync();
        var foreign = await service.ListArtifactsAsync(otherTeam, pack, PackArtifactKind.Agent, search: null, page: 0, pageSize: 10, CancellationToken.None);
        foreign.Total.ShouldBe(0, "a pack outside the caller's team yields an empty page, never another team's artifacts");
        foreign.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Mediator_threads_the_current_team_for_paged_artifacts()
    {
        var (teamA, userA) = await SeedTeamAsync();
        var (teamB, userB) = await SeedTeamAsync();

        var packInA = await SeedPackAsync(teamA, userA, "obra/superpowers", PackKind.Github, "https://github.com/obra/superpowers", "v6.0.3", null, null);
        await SeedAgentAsync(teamA, userA, "backend-architect", packInA, deleted: false);

        using (var owner = _fixture.BeginScopeAs(userA, teamA, Roles.Admin))
        {
            var page = await owner.Resolve<IMediator>().Send(new ListPackArtifactsQuery { PackId = packInA, Kind = PackArtifactKind.Agent });
            page.Items.Select(a => a.Slug).ShouldBe(new[] { "backend-architect" });
        }

        // Team B presenting A's pack id: the handler uses B's ICurrentTeam (never the payload), so A's artifacts are invisible.
        using var attacker = _fixture.BeginScopeAs(userB, teamB, Roles.Admin);
        var b = await attacker.Resolve<IMediator>().Send(new ListPackArtifactsQuery { PackId = packInA, Kind = PackArtifactKind.Agent });
        b.Items.ShouldBeEmpty("a foreign team's pack artifacts MUST NOT leak through the paged endpoint");
        b.Total.ShouldBe(0);
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

    // A pack artifact defaults to a STORE snapshot — that's the Library shape PackService surfaces. Pass scope=Working
    // to seed a grandfathered import (Imported + Working) or an authored row, which the Library must NOT count.
    private async Task SeedAgentAsync(Guid teamId, Guid userId, string slug, Guid? packId, bool deleted, DefinitionScope scope = DefinitionScope.Store, string? name = null, string? sourcePath = null)
    {
        using var s = _fixture.BeginScope();
        var db = s.Resolve<CodeSpaceDbContext>();
        db.AgentDefinition.Add(new AgentDefinition { Id = Guid.NewGuid(), TeamId = teamId, Slug = slug, Name = name ?? slug, Origin = packId == null ? AgentDefinitionOrigin.Authored : AgentDefinitionOrigin.Imported, Scope = scope, PackId = packId, SourcePath = packId == null ? null : (sourcePath ?? $"agents/{slug}.md"), DeletedDate = deleted ? DateTimeOffset.UtcNow : null, CreatedDate = DateTimeOffset.UtcNow, CreatedBy = userId, LastModifiedDate = DateTimeOffset.UtcNow, LastModifiedBy = userId });
        await db.SaveChangesAsync();
    }

    private async Task SeedSkillAsync(Guid teamId, Guid userId, string slug, Guid? packId, bool deleted, DefinitionScope scope = DefinitionScope.Store, string? name = null)
    {
        using var s = _fixture.BeginScope();
        var db = s.Resolve<CodeSpaceDbContext>();
        db.SkillDefinition.Add(new SkillDefinition { Id = Guid.NewGuid(), TeamId = teamId, Slug = slug, Name = name ?? slug, Origin = packId == null ? SkillDefinitionOrigin.Authored : SkillDefinitionOrigin.Imported, Scope = scope, PackId = packId, SourcePath = packId == null ? null : $"skills/{slug}/SKILL.md", DeletedDate = deleted ? DateTimeOffset.UtcNow : null, CreatedDate = DateTimeOffset.UtcNow, CreatedBy = userId, LastModifiedDate = DateTimeOffset.UtcNow, LastModifiedBy = userId });
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
