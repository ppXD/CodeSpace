using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Persistence;

/// <summary>
/// Persistence round-trip for the Skill library (<see cref="SkillDefinition"/> + its <see cref="Pack"/> source).
/// Proves the verbatim-frontmatter jsonb (lossless forward-compat), the enum-as-string mapping, the xmin
/// concurrency token, the slug partial-unique index (unique per team among non-deleted, reusable after
/// soft-delete), and the UNIFIED SYNC INDEX uq_skill_definition_pack_source (one active row per (pack, file),
/// so a re-sync can't duplicate). Real Postgres via the migration (Rule 12).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class SkillDefinitionPersistenceTests
{
    private readonly PostgresFixture _fixture;

    public SkillDefinitionPersistenceTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Round_trips_all_fields_including_verbatim_frontmatter_enum_and_xmin()
    {
        var teamId = await SeedTeamAsync();
        var packId = await InsertPackAsync(teamId, "github-pack");
        var id = Guid.NewGuid();

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.SkillDefinition.Add(NewSkill(id, teamId, "test-driven-development", packId));
            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var s = await db.SkillDefinition.SingleAsync(x => x.Id == id);

            s.Slug.ShouldBe("test-driven-development");
            s.Name.ShouldBe("Test Driven Development");
            s.Description.ShouldBe("Use when implementing any feature or bugfix.");
            s.Body.ShouldBe("Write the test first.");
            s.Category.ShouldBe("testing");
            s.Origin.ShouldBe(SkillDefinitionOrigin.Imported);          // enum-as-string round-trips
            s.PackId.ShouldBe(packId);
            s.SourcePath.ShouldBe("skills/test-driven-development/SKILL.md");

            // jsonb preserves semantics (keys/values), not byte-exact formatting — assert structurally.
            JsonDocument.Parse(s.RawFrontmatterJson).RootElement.GetProperty("custom_future_key").GetInt32().ShouldBe(42);  // unknown key survives
            s.DeletedDate.ShouldBeNull();
            s.Xmin.ShouldNotBe(0u);                                     // concurrency token populated by Postgres
        }
    }

    [Fact]
    public async Task Pack_round_trips_kind_enum_and_xmin()
    {
        var teamId = await SeedTeamAsync();
        var packId = await InsertPackAsync(teamId, "wshobson-agents");

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var p = await db.Pack.SingleAsync(x => x.Id == packId);

        p.Kind.ShouldBe(PackKind.Github);
        p.Name.ShouldBe("wshobson-agents");
        p.Url.ShouldBe("wshobson/agents");
        p.Xmin.ShouldNotBe(0u);
    }

    [Fact]
    public async Task Authored_skill_round_trips_origin_and_null_provenance()
    {
        var teamId = await SeedTeamAsync();
        var id = Guid.NewGuid();

        await InsertAsync(NewSkill(id, teamId, "writing-plans", packId: null));

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var s = await db.SkillDefinition.SingleAsync(x => x.Id == id);

        s.Origin.ShouldBe(SkillDefinitionOrigin.Authored);   // the Authored enum string round-trips too (not just Imported)
        s.PackId.ShouldBeNull();
        s.SourcePath.ShouldBeNull();
    }

    [Fact]
    public async Task Pack_is_unique_per_team_source_but_allows_multiple_custom_and_reuse_after_soft_delete()
    {
        var teamId = await SeedTeamAsync();

        var first = NewPack(teamId, PackKind.Github, "wshobson", "wshobson/agents", subpath: null);
        await InsertPackEntityAsync(first);

        // Same (team, url, subpath=null): COALESCE(subpath,'') makes the NULLs collide on the partial unique index.
        await Should.ThrowAsync<DbUpdateException>(() => InsertPackEntityAsync(NewPack(teamId, PackKind.Github, "dup", "wshobson/agents", subpath: null)));

        // Same url, DIFFERENT subpath → distinct source, no collision (two packs from different subdirs of one repo).
        await Should.NotThrowAsync(() => InsertPackEntityAsync(NewPack(teamId, PackKind.Github, "sub", "wshobson/agents", subpath: "plugins")));

        // The Custom pack has url=null; the `url IS NOT NULL` partial clause excludes it, so a team may hold many.
        await Should.NotThrowAsync(() => InsertPackEntityAsync(NewPack(teamId, PackKind.Custom, "custom-a", url: null, subpath: null)));
        await Should.NotThrowAsync(() => InsertPackEntityAsync(NewPack(teamId, PackKind.Custom, "custom-b", url: null, subpath: null)));

        // Soft-delete the first github pack, then the same source is free to re-add (partial index excludes deleted).
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var row = await db.Pack.SingleAsync(x => x.Id == first.Id);
            row.DeletedDate = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        await Should.NotThrowAsync(() => InsertPackEntityAsync(NewPack(teamId, PackKind.Github, "re-add", "wshobson/agents", subpath: null)));
    }

    [Fact]
    public async Task Slug_is_unique_per_team_among_active_but_reusable_after_soft_delete()
    {
        var teamId = await SeedTeamAsync();

        await InsertAsync(NewSkill(Guid.NewGuid(), teamId, "systematic-debugging", packId: null));

        // A second ACTIVE skill with the same (team, slug) violates the partial unique index.
        await Should.ThrowAsync<DbUpdateException>(() => InsertAsync(NewSkill(Guid.NewGuid(), teamId, "systematic-debugging", packId: null)));

        await SoftDeleteSkillAsync(teamId, "systematic-debugging");

        // Soft-deleted → the slug is free to reuse (partial index excludes deleted rows).
        await Should.NotThrowAsync(() => InsertAsync(NewSkill(Guid.NewGuid(), teamId, "systematic-debugging", packId: null)));
    }

    [Fact]
    public async Task Pack_and_source_path_are_unique_among_active_so_a_resync_cannot_duplicate()
    {
        var teamId = await SeedTeamAsync();
        var packId = await InsertPackAsync(teamId, "github-pack");

        var first = NewSkill(Guid.NewGuid(), teamId, "tdd", packId);
        await InsertAsync(first);

        // The SAME (pack, source_path) imported again as a NEW active row violates the sync identity index —
        // forcing the importer to UPSERT the existing row instead of duplicating it.
        var dup = NewSkill(Guid.NewGuid(), teamId, "tdd-2", packId);   // different slug, SAME source path
        await Should.ThrowAsync<DbUpdateException>(() => InsertAsync(dup));

        // Once the original is soft-deleted, the (pack, source_path) is free to re-import.
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var row = await db.SkillDefinition.SingleAsync(x => x.Id == first.Id);
            row.DeletedDate = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        await Should.NotThrowAsync(() => InsertAsync(NewSkill(Guid.NewGuid(), teamId, "tdd-2", packId)));
    }

    private static SkillDefinition NewSkill(Guid id, Guid teamId, string slug, Guid? packId) => new()
    {
        Id = id,
        TeamId = teamId,
        Slug = slug,
        Name = "Test Driven Development",
        Description = "Use when implementing any feature or bugfix.",
        Body = "Write the test first.",
        Category = "testing",
        RawFrontmatterJson = "{\"name\":\"test-driven-development\",\"custom_future_key\":42}",
        Origin = packId is null ? SkillDefinitionOrigin.Authored : SkillDefinitionOrigin.Imported,
        PackId = packId,
        SourcePath = packId is null ? null : "skills/test-driven-development/SKILL.md",
        CreatedDate = DateTimeOffset.UtcNow,
        CreatedBy = Guid.NewGuid(),
        LastModifiedDate = DateTimeOffset.UtcNow,
        LastModifiedBy = Guid.NewGuid(),
    };

    private async Task<Guid> InsertPackAsync(Guid teamId, string name)
    {
        var pack = NewPack(teamId, PackKind.Github, name, "wshobson/agents", subpath: null);
        await InsertPackEntityAsync(pack);
        return pack.Id;
    }

    private static Pack NewPack(Guid teamId, PackKind kind, string name, string? url, string? subpath) => new()
    {
        Id = Guid.NewGuid(),
        TeamId = teamId,
        Kind = kind,
        Name = name,
        Url = url,
        Reference = url is null ? null : "main",
        Subpath = subpath,
        CreatedDate = DateTimeOffset.UtcNow,
        CreatedBy = Guid.NewGuid(),
        LastModifiedDate = DateTimeOffset.UtcNow,
        LastModifiedBy = Guid.NewGuid(),
    };

    private async Task InsertPackEntityAsync(Pack pack)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.Pack.Add(pack);
        await db.SaveChangesAsync();
    }

    private async Task InsertAsync(SkillDefinition skill)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.SkillDefinition.Add(skill);
        await db.SaveChangesAsync();
    }

    private async Task SoftDeleteSkillAsync(Guid teamId, string slug)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var row = await db.SkillDefinition.SingleAsync(x => x.TeamId == teamId && x.Slug == slug && x.DeletedDate == null);
        row.DeletedDate = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"skill-{userId:N}@test.local", Name = $"skill-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"skill-{teamId:N}", Name = "Skill Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return teamId;
    }
}
