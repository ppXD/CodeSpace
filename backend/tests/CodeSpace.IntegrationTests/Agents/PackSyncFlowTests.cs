using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Identity;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// The store's Sync action (POST /api/packs/{id}/sync) through the REAL mediator + a real git clone of a LOCAL
/// repo. Proves: re-pulling refreshes a changed artifact (Updated), leaves an unchanged one alone (UpToDate),
/// and surfaces a discovered-but-not-imported artifact as a preview WITHOUT importing it; and that syncing an
/// unknown or source-less pack throws.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class PackSyncFlowTests
{
    private readonly PostgresFixture _fixture;

    public PackSyncFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Sync_splits_up_to_date_vs_updated_across_kinds_and_change_kinds()
    {
        if (OperatingSystem.IsWindows()) return;

        using var seedScope = _fixture.BeginScope();
        var runners = seedScope.Resolve<ISandboxRunnerRegistry>();
        if (!await GitReadyAsync(runners)) return;

        var (teamId, userId) = await SeedTeamAsync();
        var src = await CreateSourceRepoAsync(runners);

        try
        {
            var commit = await SendAsync(teamId, userId, new ImportPackFromUrlCommand
            {
                Url = src,
                SourcePaths = new[] { "agents/reviewer.md", "agents/architect.md", "agents/toolsmith.md", "agents/frontmatter-agent.md", "skills/tdd/SKILL.md", "skills/debugging/SKILL.md" },
            });
            var packId = commit.PackId;

            var unchangedBefore = await LastModifiedAsync(teamId, "architect", "debugging", "frontmatter-agent");

            // Each change kind exercised: a body change (agent + skill), a tools-only change, a frontmatter-only
            // (cosmetic) change → UpToDate, two artifacts left byte-identical, and a brand-new skill.
            await RewriteAndCommitAsync(runners, src, new[]
            {
                ("agents/reviewer.md", "---\nname: reviewer\ndescription: Reviews PRs.\n---\nYou review v2 now."),               // body → Updated
                ("agents/toolsmith.md", "---\nname: toolsmith\ndescription: Tools.\ntools: Read, Grep\n---\nYou tool."),         // tools → Updated
                ("agents/frontmatter-agent.md", "---\nname: frontmatter-agent\ndescription: FM.\ncolor: purple\n---\nYou fm."),  // frontmatter only → UpToDate
                ("skills/tdd/SKILL.md", "---\nname: tdd\ndescription: Use when implementing.\n---\n# TDD v2"),                    // body → Updated
                ("skills/new-skill/SKILL.md", "---\nname: new-skill\ndescription: Brand new.\n---\n# New"),                      // new → preview only
                // architect + debugging untouched → UpToDate
            });

            var sync = await SendAsync(teamId, userId, new SyncPackCommand { PackId = packId });

            sync.PackId.ShouldBe(packId);
            sync.Updated.ShouldBe(3, "reviewer body, toolsmith tools, tdd body");
            sync.UpToDate.ShouldBe(3, "architect + debugging unchanged, frontmatter-agent changed only its frontmatter");
            sync.NewArtifacts.Skills.Single(s => s.DerivedSlug == "new-skill").Importable.ShouldBeTrue();

            await AssertStateAsync(async db =>
            {
                (await db.AgentDefinition.SingleAsync(a => a.TeamId == teamId && a.Slug == "reviewer" && a.DeletedDate == null)).SystemPrompt.ShouldContain("v2 now");
                (await db.AgentDefinition.SingleAsync(a => a.TeamId == teamId && a.Slug == "toolsmith" && a.DeletedDate == null)).ToolsJson!.ShouldContain("Grep", customMessage: "a tools-only change refreshes the tool list");
                (await db.SkillDefinition.SingleAsync(s => s.TeamId == teamId && s.Slug == "tdd" && s.DeletedDate == null)).Body.ShouldContain("# TDD v2");

                (await db.SkillDefinition.AnyAsync(s => s.TeamId == teamId && s.Slug == "new-skill" && s.DeletedDate == null)).ShouldBeFalse("a sync surfaces new artifacts but never auto-imports them");
            });

            // The UpToDate artifacts were not written — their LastModified did not move (incl. the frontmatter-only one).
            var unchangedAfter = await LastModifiedAsync(teamId, "architect", "debugging", "frontmatter-agent");
            foreach (var slug in unchangedBefore.Keys)
                unchangedAfter[slug].ShouldBe(unchangedBefore[slug], customMessage: $"{slug} is UpToDate and must issue no write");
        }
        finally
        {
            try { Directory.Delete(src, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Sync_of_another_teams_pack_is_not_found()
    {
        var (teamA, userA) = await SeedTeamAsync();
        var (teamB, userB) = await SeedTeamAsync();

        var packInB = await SeedRemotePackAsync(teamB, userB);   // owned by B, with a remote URL (otherwise syncable)

        // Team A presenting B's pack id: the team-scoped load finds nothing → not-found, no clone, no leak.
        await Should.ThrowAsync<KeyNotFoundException>(() => SendAsync(teamA, userA, new SyncPackCommand { PackId = packInB }));
    }

    [Fact]
    public async Task Sync_throws_for_an_unknown_or_source_less_pack()
    {
        var (teamId, userId) = await SeedTeamAsync();

        await Should.ThrowAsync<KeyNotFoundException>(() => SendAsync(teamId, userId, new SyncPackCommand { PackId = Guid.NewGuid() }));

        var custom = await SeedCustomPackAsync(teamId, userId);
        await Should.ThrowAsync<PackImportException>(() => SendAsync(teamId, userId, new SyncPackCommand { PackId = custom }));
    }

    private async Task<T> SendAsync<T>(Guid teamId, Guid userId, IRequest<T> request)
    {
        using var scope = _fixture.BeginScope(b =>
        {
            b.RegisterInstance(new TestCurrentUser(userId, "test", Roles.Admin)).As<ICurrentUser>().SingleInstance();
            b.RegisterInstance(new TestCurrentTeam(teamId)).As<ICurrentTeam>().SingleInstance();
            b.Register(c => new PackCloneFetcher(new AllowAll(), c.Resolve<ISandboxRunnerRegistry>(), NullLogger<PackCloneFetcher>.Instance))
                .As<IPackSourceFetcher>().InstancePerLifetimeScope();
        });

        return await scope.Resolve<IMediator>().Send(request);
    }

    private async Task AssertStateAsync(Func<CodeSpaceDbContext, Task> assert)
    {
        using var scope = _fixture.BeginScope();
        await assert(scope.Resolve<CodeSpaceDbContext>());
    }

    private sealed class AllowAll : IPackHostAllowlist
    {
        public bool IsAllowed(string url) => true;
        public void EnsureAllowed(string url) { }
    }

    private async Task<string> CreateSourceRepoAsync(ISandboxRunnerRegistry runners)
    {
        var src = Path.Combine(Path.GetTempPath(), "cs-syncsrc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(src);

        await RunGitAsync(runners, new[] { "init", "-b", "main", src });
        WriteFile(src, "agents/reviewer.md", "---\nname: reviewer\ndescription: Reviews PRs.\n---\nYou review v1.");
        WriteFile(src, "agents/architect.md", "---\nname: architect\ndescription: Designs.\n---\nYou design.");
        WriteFile(src, "agents/toolsmith.md", "---\nname: toolsmith\ndescription: Tools.\ntools: Read\n---\nYou tool.");
        WriteFile(src, "agents/frontmatter-agent.md", "---\nname: frontmatter-agent\ndescription: FM.\n---\nYou fm.");
        WriteFile(src, "skills/tdd/SKILL.md", "---\nname: tdd\ndescription: Use when implementing.\n---\n# TDD v1");
        WriteFile(src, "skills/debugging/SKILL.md", "---\nname: debugging\ndescription: Debug.\n---\n# Debug");
        await CommitAllAsync(runners, src, "init");
        return src;
    }

    private async Task RewriteAndCommitAsync(ISandboxRunnerRegistry runners, string src, IReadOnlyList<(string Path, string Content)> files)
    {
        foreach (var (path, content) in files) WriteFile(src, path, content);
        await CommitAllAsync(runners, src, "update");
    }

    private static async Task CommitAllAsync(ISandboxRunnerRegistry runners, string src, string message)
    {
        await RunGitAsync(runners, new[] { "-C", src, "-c", "user.email=t@codespace.test", "-c", "user.name=Test", "add", "-A" });
        await RunGitAsync(runners, new[] { "-C", src, "-c", "user.email=t@codespace.test", "-c", "user.name=Test", "commit", "-m", message });
    }

    private static async Task<bool> GitReadyAsync(ISandboxRunnerRegistry runners)
    {
        try { return (await runners.Resolve(LocalProcessRunner.LocalKind).RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 15 }, default)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    private static async Task RunGitAsync(ISandboxRunnerRegistry runners, string[] args)
    {
        var r = await runners.Resolve(LocalProcessRunner.LocalKind).RunAsync(new SandboxSpec { Command = "git", Args = args, TimeoutSeconds = 30 }, default);
        r.Status.ShouldBe(SandboxStatus.Success, $"git {string.Join(' ', args)} failed (exit {r.ExitCode}): {r.Stderr}");
    }

    private static void WriteFile(string root, string relPath, string content)
    {
        var full = Path.Combine(root, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private async Task<Dictionary<string, DateTimeOffset>> LastModifiedAsync(Guid teamId, params string[] slugs)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var map = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var a in await db.AgentDefinition.AsNoTracking().Where(a => a.TeamId == teamId && slugs.Contains(a.Slug) && a.DeletedDate == null).Select(a => new { a.Slug, a.LastModifiedDate }).ToListAsync())
            map[a.Slug] = a.LastModifiedDate;
        foreach (var s in await db.SkillDefinition.AsNoTracking().Where(s => s.TeamId == teamId && slugs.Contains(s.Slug) && s.DeletedDate == null).Select(s => new { s.Slug, s.LastModifiedDate }).ToListAsync())
            map[s.Slug] = s.LastModifiedDate;
        return map;
    }

    private async Task<Guid> SeedRemotePackAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var id = Guid.NewGuid();
        db.Pack.Add(new Pack { Id = id, TeamId = teamId, Kind = PackKind.Github, Name = "owner/repo", Url = "https://github.com/owner/repo", Reference = "main", CreatedDate = DateTimeOffset.UtcNow, CreatedBy = userId, LastModifiedDate = DateTimeOffset.UtcNow, LastModifiedBy = userId });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedCustomPackAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var id = Guid.NewGuid();
        db.Pack.Add(new Pack { Id = id, TeamId = teamId, Kind = PackKind.Custom, Name = "Custom", Url = null, CreatedDate = DateTimeOffset.UtcNow, CreatedBy = userId, LastModifiedDate = DateTimeOffset.UtcNow, LastModifiedBy = userId });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<(Guid TeamId, Guid UserId)> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"sync-{userId:N}@test.local", Name = $"sync-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"sync-{teamId:N}", Name = "Sync Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return (teamId, userId);
    }
}
