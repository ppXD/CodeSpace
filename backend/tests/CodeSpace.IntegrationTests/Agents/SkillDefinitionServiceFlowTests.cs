using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Skills;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// The skill-library CRUD vertical end-to-end through <see cref="ISkillDefinitionService"/> + real Postgres.
/// Proves name → handle derivation, the Level-1 list (body omitted) vs Level-2 get (body included), authorable
/// edits, per-team handle uniqueness with reuse after soft-delete, and cross-team isolation.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class SkillDefinitionServiceFlowTests
{
    private readonly PostgresFixture _fixture;

    public SkillDefinitionServiceFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Create_then_Get_round_trips_with_a_derived_handle()
    {
        var (teamId, userId) = await SeedTeamAsync();

        Guid id;
        using (var scope = _fixture.BeginScope())
        {
            id = await scope.Resolve<ISkillDefinitionService>().CreateAsync(teamId, new SkillDefinitionInput
            {
                Name = "Test Driven Development",
                Description = "Use when implementing any feature or bugfix.",
                Body = "Write the test first.",
                Category = "testing",
            }, userId, default);
        }

        using var verify = _fixture.BeginScope();
        var loaded = await verify.Resolve<ISkillDefinitionService>().GetAsync(teamId, id, default);

        loaded.ShouldNotBeNull();
        loaded!.Slug.ShouldBe("test-driven-development", customMessage: "the handle is derived from the name");
        loaded.Name.ShouldBe("Test Driven Development");
        loaded.Description.ShouldBe("Use when implementing any feature or bugfix.");
        loaded.Body.ShouldBe("Write the test first.");
        loaded.Category.ShouldBe("testing");
        loaded.Origin.ShouldBe(SkillDefinitionOrigin.Authored);
        loaded.PackId.ShouldBeNull();
    }

    [Fact]
    public async Task List_is_level1_and_does_not_carry_the_body()
    {
        var (teamId, userId) = await SeedTeamAsync();
        await CreateSkillAsync(teamId, userId, "Brainstorming", body: "A long Level-2 instruction body.");

        using var scope = _fixture.BeginScope();
        var list = await scope.Resolve<ISkillDefinitionService>().ListAsync(teamId, default);

        list.Count.ShouldBe(1);
        // SkillDefinitionSummary has no Body member at all — the list is Level-1 by construction. Assert the
        // shape carries the routing fields the store UI needs.
        list[0].Name.ShouldBe("Brainstorming");
        list[0].Slug.ShouldBe("brainstorming");
    }

    [Fact]
    public async Task Update_edits_the_authorable_fields()
    {
        var (teamId, userId) = await SeedTeamAsync();
        var id = await CreateSkillAsync(teamId, userId, "Writing Plans", body: "v1");

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<ISkillDefinitionService>().UpdateAsync(teamId, id, new SkillDefinitionInput { Name = "Writing Plans", Description = "Use when planning.", Body = "v2", Category = "process" }, userId, default);

        using var verify = _fixture.BeginScope();
        var loaded = await verify.Resolve<ISkillDefinitionService>().GetAsync(teamId, id, default);

        loaded!.Body.ShouldBe("v2");
        loaded.Description.ShouldBe("Use when planning.");
        loaded.Category.ShouldBe("process");
        loaded.Slug.ShouldBe("writing-plans", customMessage: "the handle is stable across edits — it must NOT be recomputed from the (possibly changed) name");
        loaded.Origin.ShouldBe(SkillDefinitionOrigin.Authored);
    }

    [Fact]
    public async Task Update_preserves_the_import_owned_fields()
    {
        var (teamId, userId) = await SeedTeamAsync();
        var packId = await InsertPackAsync(teamId);
        var id = await InsertImportedSkillAsync(teamId, packId, "imported-skill");

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<ISkillDefinitionService>().UpdateAsync(teamId, id, new SkillDefinitionInput { Name = "Renamed Skill", Body = "edited body" }, userId, default);

        using var verify = _fixture.BeginScope();
        var loaded = await verify.Resolve<ISkillDefinitionService>().GetAsync(teamId, id, default);

        // The authored edit lands…
        loaded!.Body.ShouldBe("edited body");
        loaded.Name.ShouldBe("Renamed Skill");

        // …but the import-owned fields are deliberately untouched, so re-sync metadata survives an authored edit.
        loaded.Slug.ShouldBe("imported-skill", customMessage: "editing must not re-derive the handle");
        loaded.Origin.ShouldBe(SkillDefinitionOrigin.Imported, customMessage: "an edit must not flip an imported skill to Authored");
        loaded.PackId.ShouldBe(packId);
        loaded.SourcePath.ShouldBe("skills/imported-skill/SKILL.md");
        loaded.RawFrontmatterJson.ShouldContain("test-driven-development");
    }

    [Fact]
    public async Task Delete_soft_deletes_so_it_leaves_the_list_and_frees_the_slug()
    {
        var (teamId, userId) = await SeedTeamAsync();
        var id = await CreateSkillAsync(teamId, userId, "Systematic Debugging", body: "x");

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<ISkillDefinitionService>().DeleteAsync(teamId, id, userId, default);

        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<ISkillDefinitionService>();
            (await svc.ListAsync(teamId, default)).ShouldBeEmpty();
            (await svc.GetAsync(teamId, id, default)).ShouldBeNull();
        }

        // The handle is free to reuse after soft-delete.
        await Should.NotThrowAsync(() => CreateSkillAsync(teamId, userId, "Systematic Debugging", body: "y"));
    }

    [Fact]
    public async Task Create_rejects_a_duplicate_handle_in_the_same_team()
    {
        var (teamId, userId) = await SeedTeamAsync();
        await CreateSkillAsync(teamId, userId, "Code Review", body: "x");

        await Should.ThrowAsync<InvalidOperationException>(() => CreateSkillAsync(teamId, userId, "code-review", body: "y"));
    }

    [Fact]
    public async Task Get_is_team_scoped()
    {
        var (teamA, userA) = await SeedTeamAsync();
        var (teamB, _) = await SeedTeamAsync();
        var id = await CreateSkillAsync(teamA, userA, "Cross Tenant", body: "x");

        using var scope = _fixture.BeginScope();
        (await scope.Resolve<ISkillDefinitionService>().GetAsync(teamB, id, default)).ShouldBeNull();
    }

    [Fact]
    public async Task List_excludes_store_snapshots()
    {
        var (teamId, userId) = await SeedTeamAsync();
        var workingId = await CreateSkillAsync(teamId, userId, "Brainstorming", body: "bench body");

        // A Library STORE snapshot lives in the store, not on the bindable bench picker — the list must exclude it.
        await SeedStoreSkillSnapshotAsync(teamId, "store-snapshot");

        using var scope = _fixture.BeginScope();
        var list = await scope.Resolve<ISkillDefinitionService>().ListAsync(teamId, default);

        list.Select(s => s.Id).ShouldBe(new[] { workingId }, "ListAsync returns only Working skills; the store snapshot is excluded");
    }

    [Fact]
    public async Task Create_succeeds_when_only_a_store_snapshot_owns_the_handle()
    {
        var (teamId, userId) = await SeedTeamAsync();

        // A Library STORE snapshot owns "code-review". Team-slug uniqueness is Working-only, so authoring a runnable
        // skill of the same name must NOT be refused — the snapshot and the bench skill coexist.
        await SeedStoreSkillSnapshotAsync(teamId, "code-review");

        await Should.NotThrowAsync(() => CreateSkillAsync(teamId, userId, "Code Review", body: "bench body"));
    }

    private async Task<Guid> CreateSkillAsync(Guid teamId, Guid userId, string name, string body)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<ISkillDefinitionService>().CreateAsync(teamId, new SkillDefinitionInput { Name = name, Body = body }, userId, default);
    }

    /// <summary>Inserts a STORE snapshot skill (Origin=Imported, Scope=Store) directly — the Library shape that must never surface on the bindable bench list.</summary>
    private async Task SeedStoreSkillSnapshotAsync(Guid teamId, string slug)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.SkillDefinition.Add(new SkillDefinition
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Slug = slug,
            Name = slug,
            Body = "snapshot body",
            Origin = SkillDefinitionOrigin.Imported,
            Scope = DefinitionScope.Store,
            PackId = null,   // pack provenance is irrelevant to the scope filter under test (and skill_definition has a real pack FK)
            SourcePath = $"skills/{slug}/SKILL.md",
            CreatedDate = DateTimeOffset.UtcNow,
            CreatedBy = Guid.NewGuid(),
            LastModifiedDate = DateTimeOffset.UtcNow,
            LastModifiedBy = Guid.NewGuid(),
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> InsertPackAsync(Guid teamId)
    {
        var id = Guid.NewGuid();
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.Pack.Add(new Pack { Id = id, TeamId = teamId, Kind = PackKind.Github, Name = "wshobson", Url = "wshobson/agents", Reference = "main", CreatedDate = DateTimeOffset.UtcNow, CreatedBy = Guid.NewGuid(), LastModifiedDate = DateTimeOffset.UtcNow, LastModifiedBy = Guid.NewGuid() });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> InsertImportedSkillAsync(Guid teamId, Guid packId, string slug)
    {
        var id = Guid.NewGuid();
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.SkillDefinition.Add(new SkillDefinition
        {
            Id = id,
            TeamId = teamId,
            Slug = slug,
            Name = "Imported Skill",
            Body = "imported body",
            Origin = SkillDefinitionOrigin.Imported,
            PackId = packId,
            SourcePath = $"skills/{slug}/SKILL.md",
            RawFrontmatterJson = "{\"name\":\"test-driven-development\"}",
            CreatedDate = DateTimeOffset.UtcNow,
            CreatedBy = Guid.NewGuid(),
            LastModifiedDate = DateTimeOffset.UtcNow,
            LastModifiedBy = Guid.NewGuid(),
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<(Guid TeamId, Guid UserId)> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"skill-{userId:N}@test.local", Name = $"skill-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"skill-{teamId:N}", Name = "Skill Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return (teamId, userId);
    }
}
