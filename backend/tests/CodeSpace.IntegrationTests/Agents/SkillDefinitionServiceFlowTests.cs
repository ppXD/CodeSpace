using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
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

    [Fact]
    public async Task Instantiate_from_store_copies_content_into_a_new_working_bindable_skill_linked_to_the_snapshot()
    {
        var (teamId, userId) = await SeedTeamAsync();
        var snapshotId = await SeedStoreSkillSnapshotAsync(teamId, "tdd");

        Guid newId;
        using (var scope = _fixture.BeginScope())
            newId = await scope.Resolve<ISkillDefinitionService>().InstantiateFromStoreAsync(teamId, snapshotId, userId, default);

        newId.ShouldNotBe(snapshotId, "instantiate creates a NEW row, not a reference to the snapshot");

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var copy = await db.SkillDefinition.AsNoTracking().SingleAsync(s => s.Id == newId);
        var snapshot = await db.SkillDefinition.AsNoTracking().SingleAsync(s => s.Id == snapshotId);

        copy.Scope.ShouldBe(DefinitionScope.Working);
        copy.Origin.ShouldBe(SkillDefinitionOrigin.Imported);
        copy.Slug.ShouldBe("tdd", "the snapshot's handle is free on the bench, so the copy keeps it");
        copy.Body.ShouldBe(snapshot.Body);
        copy.Description.ShouldBe(snapshot.Description);
        copy.Category.ShouldBe(snapshot.Category);
        copy.RawFrontmatterJson.ShouldContain("custom_future_key");
        copy.SourceDefinitionId.ShouldBe(snapshotId);
        copy.SourceVersion.ShouldBe("v-tdd", "the copy captures the snapshot's content version as the LHS of a future sync");
        copy.PackId.ShouldBeNull();

        // It's bindable — it appears on the Working skill list (the editor picker), while the snapshot stays in the Library.
        var list = await verify.Resolve<ISkillDefinitionService>().ListAsync(teamId, default);
        list.Select(s => s.Id).ShouldContain(newId);
        list.Select(s => s.Id).ShouldNotContain(snapshotId);

        // The list summary carries the provenance link, so the binding picker can recognise this working skill as a
        // copy of its store snapshot across editor re-opens (and not mint a duplicate).
        list.Single(s => s.Id == newId).SourceDefinitionId.ShouldBe(snapshotId);
    }

    [Fact]
    public async Task Instantiate_from_store_auto_disambiguates_a_handle_a_bench_skill_owns()
    {
        var (teamId, userId) = await SeedTeamAsync();
        await CreateSkillAsync(teamId, userId, "TDD", body: "bench");   // bench already owns "tdd"
        var snapshotId = await SeedStoreSkillSnapshotAsync(teamId, "tdd");

        Guid newId;
        using (var scope = _fixture.BeginScope())
            newId = await scope.Resolve<ISkillDefinitionService>().InstantiateFromStoreAsync(teamId, snapshotId, userId, default);

        using var verify = _fixture.BeginScope();
        var copy = await verify.Resolve<CodeSpaceDbContext>().SkillDefinition.AsNoTracking().SingleAsync(s => s.Id == newId);
        copy.Slug.ShouldBe("tdd-2", "the handle is taken on the bench, so instantiate suffixes rather than dead-ending");
    }

    [Fact]
    public async Task Instantiate_from_store_reuses_a_free_working_copy_instead_of_piling_up_duplicates()
    {
        var (teamId, userId) = await SeedTeamAsync();
        var snapshotId = await SeedStoreSkillSnapshotAsync(teamId, "tdd");

        Guid first, second;
        using (var scope = _fixture.BeginScope())
            first = await scope.Resolve<ISkillDefinitionService>().InstantiateFromStoreAsync(teamId, snapshotId, userId, default);

        // The first copy is unbound (no agent carries it), so a second instantiate REUSES it rather than minting tdd-2.
        using (var scope = _fixture.BeginScope())
            second = await scope.Resolve<ISkillDefinitionService>().InstantiateFromStoreAsync(teamId, snapshotId, userId, default);

        second.ShouldBe(first, "a free working copy of the snapshot is reused, not duplicated");

        using var verify = _fixture.BeginScope();
        var copies = await verify.Resolve<CodeSpaceDbContext>().SkillDefinition.AsNoTracking()
            .Where(s => s.TeamId == teamId && s.Scope == DefinitionScope.Working && s.SourceDefinitionId == snapshotId && s.DeletedDate == null)
            .CountAsync();
        copies.ShouldBe(1, "no orphaned -2/-3 duplicate piled up");
    }

    [Fact]
    public async Task Instantiate_from_store_mints_a_fresh_copy_when_the_only_existing_one_is_bound()
    {
        var (teamId, userId) = await SeedTeamAsync();
        var snapshotId = await SeedStoreSkillSnapshotAsync(teamId, "tdd");

        Guid first;
        using (var scope = _fixture.BeginScope())
            first = await scope.Resolve<ISkillDefinitionService>().InstantiateFromStoreAsync(teamId, snapshotId, userId, default);

        // Bind the first copy to an agent — it is no longer FREE.
        Guid agentId;
        using (var scope = _fixture.BeginScope())
            agentId = await scope.Resolve<IAgentDefinitionService>().CreateAsync(teamId, new AgentDefinitionInput { Name = "Reviewer" }, userId, default);
        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentSkillBindingService>().SetForAgentAsync(teamId, agentId, new[] { first }, userId, default);

        Guid second;
        using (var scope = _fixture.BeginScope())
            second = await scope.Resolve<ISkillDefinitionService>().InstantiateFromStoreAsync(teamId, snapshotId, userId, default);

        second.ShouldNotBe(first, "the only copy is bound to an agent, so a fresh private copy is minted (per-agent isolation preserved)");

        using var verify = _fixture.BeginScope();
        var copy = await verify.Resolve<CodeSpaceDbContext>().SkillDefinition.AsNoTracking().SingleAsync(s => s.Id == second);
        copy.Slug.ShouldBe("tdd-2", "the bound copy owns 'tdd', so the fresh copy disambiguates");
    }

    [Fact]
    public async Task Instantiate_from_store_rejects_a_non_store_or_foreign_id()
    {
        var (teamId, userId) = await SeedTeamAsync();
        var (otherTeam, otherUser) = await SeedTeamAsync();

        // A WORKING bench skill is not a store snapshot → not instantiable.
        var workingId = await CreateSkillAsync(teamId, userId, "Bench Skill", body: "x");
        using (var scope = _fixture.BeginScope())
            await Should.ThrowAsync<KeyNotFoundException>(() => scope.Resolve<ISkillDefinitionService>().InstantiateFromStoreAsync(teamId, workingId, userId, default));

        // A foreign team's store snapshot → not accessible (no cross-team read).
        var foreignSnapshot = await SeedStoreSkillSnapshotAsync(otherTeam, "foreign-skill");
        using (var scope = _fixture.BeginScope())
            await Should.ThrowAsync<KeyNotFoundException>(() => scope.Resolve<ISkillDefinitionService>().InstantiateFromStoreAsync(teamId, foreignSnapshot, userId, default));
    }

    [Fact]
    public async Task Author_into_library_creates_a_store_skill_under_the_custom_pack_off_the_bench()
    {
        var (teamId, userId) = await SeedTeamAsync();

        // Drive through the mediator so the command handler's field mapping + identity wiring are exercised (not just
        // the service in isolation), matching the agent author test.
        Guid id;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            id = await scope.Resolve<IMediator>().Send(new AuthorStoreSkillCommand { Name = "Threat Modeling", Body = "STRIDE.", Category = "security" });

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var customPack = await db.Pack.AsNoTracking().SingleAsync(p => p.TeamId == teamId && p.Kind == PackKind.Custom && p.DeletedDate == null);
        customPack.Name.ShouldBe("Custom");

        var skill = await db.SkillDefinition.AsNoTracking().SingleAsync(s => s.Id == id);
        skill.Origin.ShouldBe(SkillDefinitionOrigin.Authored);
        skill.Scope.ShouldBe(DefinitionScope.Store, "an authored Library skill lives in the store, not on the bindable bench");
        skill.PackId.ShouldBe(customPack.Id);
        skill.Body.ShouldBe("STRIDE.");

        // Off the bindable bench list (you instantiate a working copy to bind it).
        var list = await verify.Resolve<ISkillDefinitionService>().ListAsync(teamId, default);
        list.Select(s => s.Id).ShouldNotContain(id);
    }

    private async Task<Guid> CreateSkillAsync(Guid teamId, Guid userId, string name, string body)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<ISkillDefinitionService>().CreateAsync(teamId, new SkillDefinitionInput { Name = name, Body = body }, userId, default);
    }

    /// <summary>Inserts a STORE snapshot skill (Origin=Imported, Scope=Store) with real content directly — the Library shape that must never surface on the bindable bench list. Returns its id.</summary>
    private async Task<Guid> SeedStoreSkillSnapshotAsync(Guid teamId, string slug)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var id = Guid.NewGuid();
        db.SkillDefinition.Add(new SkillDefinition
        {
            Id = id,
            TeamId = teamId,
            Slug = slug,
            Name = slug,
            Description = $"{slug} description",
            Body = $"# {slug}\nDo the thing.",
            Category = "testing",
            RawFrontmatterJson = "{\"name\":\"" + slug + "\",\"custom_future_key\":7}",
            Origin = SkillDefinitionOrigin.Imported,
            Scope = DefinitionScope.Store,
            ContentVersion = "v-" + slug,
            PackId = null,   // pack provenance is irrelevant to the scope filter under test (and skill_definition has a real pack FK)
            SourcePath = $"skills/{slug}/SKILL.md",
            CreatedDate = DateTimeOffset.UtcNow,
            CreatedBy = Guid.NewGuid(),
            LastModifiedDate = DateTimeOffset.UtcNow,
            LastModifiedBy = Guid.NewGuid(),
        });
        await db.SaveChangesAsync();
        return id;
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
