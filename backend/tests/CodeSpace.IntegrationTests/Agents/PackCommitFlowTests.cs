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
/// The COMMIT half of the URL pack flow, driven through the REAL mediator (so <c>TransactionalBehavior</c>'s
/// single ambient transaction wraps it — the production path) + a real git clone of a LOCAL repo (no network).
/// The transient <c>IPackSourceFetcher</c> is overridden per-scope with an allowlist-bypassing clone so a local
/// path is reachable; everything else (walker, DB, transaction) is real. Proves: a mixed commit
/// (Imported/Skipped/Failed) is atomic + persists; a re-sync UPSERTS the row CONTENT while keeping the handle;
/// a handle that collides with a DIFFERENT active definition is Skipped (agents AND skills); and a soft-deleted
/// prior import re-imports as a fresh row rather than colliding.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class PackCommitFlowTests
{
    private readonly PostgresFixture _fixture;

    public PackCommitFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_mixed_commit_imports_skips_conflicts_and_fails_unknowns_atomically_through_the_mediator()
    {
        if (OperatingSystem.IsWindows()) return;

        using var seedScope = _fixture.BeginScope();
        if (!await GitReadyAsync(seedScope.Resolve<ISandboxRunnerRegistry>())) return;

        var (teamId, userId) = await SeedTeamAsync();
        await SeedAgentAsync(teamId, userId, "backend-architect");   // an authored persona the pack agent collides with
        await SeedSkillAsync(teamId, userId, "tdd");                 // an authored skill the pack skill collides with
        var src = await CreateSourceRepoAsync(seedScope.Resolve<ISandboxRunnerRegistry>());

        try
        {
            var result = await CommitViaMediatorAsync(src, new[]
            {
                "agents/backend-architect.md",        // conflict -> Skipped
                "agents/code-reviewer.md",            // new       -> Imported
                "skills/tdd/SKILL.md",                // conflict -> Skipped
                "skills/systematic-debugging/SKILL.md", // new     -> Imported
                "agents/ghost.md",                    // unknown   -> Failed
            }, teamId, userId);

            Outcome(result, "agents/backend-architect.md").ShouldBe(PackImportOutcome.Skipped);
            Outcome(result, "agents/code-reviewer.md").ShouldBe(PackImportOutcome.Imported);
            Outcome(result, "skills/tdd/SKILL.md").ShouldBe(PackImportOutcome.Skipped);
            Outcome(result, "skills/systematic-debugging/SKILL.md").ShouldBe(PackImportOutcome.Imported);

            var ghost = result.Items.Single(i => i.SourcePath == "agents/ghost.md");
            ghost.Outcome.ShouldBe(PackImportOutcome.Failed);
            ghost.Kind.ShouldBeNull();

            await AssertStateAsync(async db =>
            {
                (await db.Pack.CountAsync(p => p.TeamId == teamId && p.DeletedDate == null)).ShouldBe(1);

                // The two NEW artifacts landed atomically, pack-rooted, in one committed transaction.
                var imported = await db.AgentDefinition.SingleAsync(a => a.TeamId == teamId && a.Slug == "code-reviewer" && a.DeletedDate == null);
                imported.Origin.ShouldBe(AgentDefinitionOrigin.Imported);
                imported.PackId.ShouldBe(result.PackId);

                (await db.SkillDefinition.AnyAsync(s => s.TeamId == teamId && s.Slug == "systematic-debugging" && s.PackId == result.PackId && s.DeletedDate == null)).ShouldBeTrue();

                // The pre-existing authored definitions are untouched (still authored, no pack), not overwritten.
                (await db.AgentDefinition.SingleAsync(a => a.TeamId == teamId && a.Slug == "backend-architect" && a.DeletedDate == null)).Origin.ShouldBe(AgentDefinitionOrigin.Authored);
                (await db.SkillDefinition.SingleAsync(s => s.TeamId == teamId && s.Slug == "tdd" && s.DeletedDate == null)).Origin.ShouldBe(SkillDefinitionOrigin.Authored);
            });
        }
        finally
        {
            try { Directory.Delete(src, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task A_resync_refreshes_row_content_and_keeps_the_handle()
    {
        if (OperatingSystem.IsWindows()) return;

        using var seedScope = _fixture.BeginScope();
        var runners = seedScope.Resolve<ISandboxRunnerRegistry>();
        if (!await GitReadyAsync(runners)) return;

        var (teamId, userId) = await SeedTeamAsync();
        var src = await CreateSourceRepoAsync(runners);
        var paths = new[] { "agents/code-reviewer.md", "skills/systematic-debugging/SKILL.md" };

        try
        {
            var first = await CommitViaMediatorAsync(src, paths, teamId, userId);
            first.Items.ShouldAllBe(i => i.Outcome == PackImportOutcome.Imported);
            var agentId = first.Items.Single(i => i.Kind == PackArtifactKind.Agent).DefinitionId;
            var skillId = first.Items.Single(i => i.Kind == PackArtifactKind.Skill).DefinitionId;

            // Mutate the source content (same files, same names) and re-commit the git repo.
            await RewriteAndCommitAsync(runners, src, new[]
            {
                ("agents/code-reviewer.md", "---\nname: code-reviewer\ndescription: Reviews v2.\n---\nYou review v2 now."),
                ("skills/systematic-debugging/SKILL.md", "---\nname: systematic-debugging\ndescription: Debug v2.\n---\n# Debug v2"),
            });

            var second = await CommitViaMediatorAsync(src, paths, teamId, userId);

            second.PackId.ShouldBe(first.PackId);
            second.Items.ShouldAllBe(i => i.Outcome == PackImportOutcome.Updated);
            second.Items.Single(i => i.Kind == PackArtifactKind.Agent).DefinitionId.ShouldBe(agentId, "a re-sync updates in place, not a new row");
            second.Items.Single(i => i.Kind == PackArtifactKind.Skill).DefinitionId.ShouldBe(skillId);

            await AssertStateAsync(async db =>
            {
                var agent = await db.AgentDefinition.SingleAsync(a => a.TeamId == teamId && a.Slug == "code-reviewer" && a.DeletedDate == null);
                agent.SystemPrompt.ShouldContain("You review v2 now", customMessage: "the Updated branch must re-apply the parsed content");
                agent.Slug.ShouldBe("code-reviewer", "the handle is identity — a re-sync never re-derives it");

                var skill = await db.SkillDefinition.SingleAsync(s => s.TeamId == teamId && s.Slug == "systematic-debugging" && s.DeletedDate == null);
                skill.Body.ShouldContain("# Debug v2");

                (await db.AgentDefinition.CountAsync(a => a.TeamId == teamId && a.PackId == first.PackId && a.DeletedDate == null)).ShouldBe(1);
            });
        }
        finally
        {
            try { Directory.Delete(src, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task A_soft_deleted_prior_import_re_imports_as_a_fresh_row()
    {
        if (OperatingSystem.IsWindows()) return;

        using var seedScope = _fixture.BeginScope();
        if (!await GitReadyAsync(seedScope.Resolve<ISandboxRunnerRegistry>())) return;

        var (teamId, userId) = await SeedTeamAsync();
        var src = await CreateSourceRepoAsync(seedScope.Resolve<ISandboxRunnerRegistry>());
        var paths = new[] { "agents/code-reviewer.md" };

        try
        {
            var first = await CommitViaMediatorAsync(src, paths, teamId, userId);
            var originalId = first.Items.Single().DefinitionId!.Value;

            // Soft-delete the imported row out-of-band, then re-commit the same path.
            await AssertStateAsync(async db =>
            {
                var row = await db.AgentDefinition.SingleAsync(a => a.Id == originalId);
                row.DeletedDate = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
            });

            var second = await CommitViaMediatorAsync(src, paths, teamId, userId);
            var reimported = second.Items.Single();

            reimported.Outcome.ShouldBe(PackImportOutcome.Imported, "a soft-deleted prior import must not block re-import");
            reimported.DefinitionId.ShouldNotBe(originalId, "re-import is a fresh row, not the resurrected soft-deleted one");

            await AssertStateAsync(async db =>
                (await db.AgentDefinition.CountAsync(a => a.TeamId == teamId && a.PackId == second.PackId && a.SourcePath == "agents/code-reviewer.md" && a.DeletedDate == null)).ShouldBe(1));
        }
        finally
        {
            try { Directory.Delete(src, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task An_intra_pack_duplicate_handle_skips_the_second_in_one_commit()
    {
        if (OperatingSystem.IsWindows()) return;

        using var seedScope = _fixture.BeginScope();
        if (!await GitReadyAsync(seedScope.Resolve<ISandboxRunnerRegistry>())) return;

        var (teamId, userId) = await SeedTeamAsync();
        var src = await CreateSourceRepoAsync(seedScope.Resolve<ISandboxRunnerRegistry>());

        try
        {
            // Two files in ONE commit derive the same handle. The fix decides this in memory (claimedSlugs), so one
            // is Imported and the other Skipped — NOT two INSERTs that 23505 and abort the whole transaction.
            var result = await CommitViaMediatorAsync(src, new[] { "agents/dup-a.md", "agents/dup-b.md" }, teamId, userId);

            result.Items.Count(i => i.Outcome == PackImportOutcome.Imported).ShouldBe(1);
            result.Items.Count(i => i.Outcome == PackImportOutcome.Skipped).ShouldBe(1);

            // Atomic + correct: the committed transaction left exactly one active agent for the shared handle.
            await AssertStateAsync(async db =>
                (await db.AgentDefinition.CountAsync(a => a.TeamId == teamId && a.Slug == "duplicate-agent" && a.DeletedDate == null)).ShouldBe(1));
        }
        finally
        {
            try { Directory.Delete(src, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task An_imported_agent_binds_its_declared_skills_resolving_handles_in_the_same_commit()
    {
        if (OperatingSystem.IsWindows()) return;

        using var seedScope = _fixture.BeginScope();
        if (!await GitReadyAsync(seedScope.Resolve<ISandboxRunnerRegistry>())) return;

        var (teamId, userId) = await SeedTeamAsync();
        var src = await CreateSourceRepoAsync(seedScope.Resolve<ISandboxRunnerRegistry>());

        try
        {
            // The agent declares skills: [tdd, systematic-debugging, ghost-skill]. tdd + systematic-debugging are
            // imported in THIS commit (resolvable same-batch); ghost-skill matches no team skill → not bound.
            var result = await CommitViaMediatorAsync(src, new[] { "agents/skilled.md", "skills/tdd/SKILL.md", "skills/systematic-debugging/SKILL.md" }, teamId, userId);

            result.Items.Count(i => i.Outcome == PackImportOutcome.Imported).ShouldBe(3);
            var agentId = result.Items.Single(i => i.SourcePath == "agents/skilled.md").DefinitionId!.Value;

            await AssertStateAsync(async db =>
            {
                var boundSlugs = await (from b in db.AgentSkillBinding
                                        join s in db.SkillDefinition on b.SkillDefinitionId equals s.Id
                                        where b.AgentDefinitionId == agentId
                                        select s.Slug).ToListAsync();

                // resolvable declared handles are bound; ghost-skill matches no team skill and is skipped
                boundSlugs.OrderBy(x => x, StringComparer.Ordinal).ShouldBe(new[] { "systematic-debugging", "tdd" });
            });
        }
        finally
        {
            try { Directory.Delete(src, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task A_resync_does_not_reseed_declared_skill_bindings()
    {
        if (OperatingSystem.IsWindows()) return;

        using var seedScope = _fixture.BeginScope();
        if (!await GitReadyAsync(seedScope.Resolve<ISandboxRunnerRegistry>())) return;

        var (teamId, userId) = await SeedTeamAsync();
        var src = await CreateSourceRepoAsync(seedScope.Resolve<ISandboxRunnerRegistry>());
        var paths = new[] { "agents/skilled.md", "skills/tdd/SKILL.md", "skills/systematic-debugging/SKILL.md" };

        try
        {
            var first = await CommitViaMediatorAsync(src, paths, teamId, userId);
            var agentId = first.Items.Single(i => i.SourcePath == "agents/skilled.md").DefinitionId!.Value;

            // Simulate the operator curating bindings via the editor: unbind tdd.
            await AssertStateAsync(async db =>
            {
                var tdd = await db.AgentSkillBinding.SingleAsync(b => b.AgentDefinitionId == agentId && db.SkillDefinition.Any(s => s.Id == b.SkillDefinitionId && s.Slug == "tdd"));
                db.AgentSkillBinding.Remove(tdd);
                await db.SaveChangesAsync();
            });

            // Re-sync the agent — it comes back Updated, and the declared-skills seeding must NOT re-add the
            // curated-away tdd binding (re-sync only refreshes content; bindings are the editor's to own).
            var second = await CommitViaMediatorAsync(src, new[] { "agents/skilled.md" }, teamId, userId);
            second.Items.Single().Outcome.ShouldBe(PackImportOutcome.Updated);

            await AssertStateAsync(async db =>
            {
                var boundSlugs = await (from b in db.AgentSkillBinding
                                        join s in db.SkillDefinition on b.SkillDefinitionId equals s.Id
                                        where b.AgentDefinitionId == agentId
                                        select s.Slug).ToListAsync();

                // a re-sync leaves curated bindings alone — tdd is not re-seeded
                boundSlugs.ShouldBe(new[] { "systematic-debugging" });
            });
        }
        finally
        {
            try { Directory.Delete(src, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static PackImportOutcome Outcome(PackImportResult result, string sourcePath) =>
        result.Items.Single(i => i.SourcePath == sourcePath).Outcome;

    /// <summary>Drive the commit through the real mediator (TransactionalBehavior wraps it) with an allowlist-bypassing fetcher so a LOCAL repo is clonable. Everything else — walker, DbContext, transaction — is the real wiring.</summary>
    private async Task<PackImportResult> CommitViaMediatorAsync(string url, IReadOnlyList<string> paths, Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScope(b =>
        {
            b.RegisterInstance(new TestCurrentUser(userId, "test", Roles.Admin)).As<ICurrentUser>().SingleInstance();
            b.RegisterInstance(new TestCurrentTeam(teamId)).As<ICurrentTeam>().SingleInstance();
            b.Register(c => new PackCloneFetcher(new AllowAll(), c.Resolve<ISandboxRunnerRegistry>(), NullLogger<PackCloneFetcher>.Instance))
                .As<IPackSourceFetcher>().InstancePerLifetimeScope();
        });

        return await scope.Resolve<IMediator>().Send(new ImportPackFromUrlCommand { Url = url, SourcePaths = paths });
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
        var src = Path.Combine(Path.GetTempPath(), "cs-commitsrc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(src);

        await RunGitAsync(runners, new[] { "init", "-b", "main", src });
        WriteFile(src, "agents/backend-architect.md", "---\nname: backend-architect\ndescription: Use for backend.\n---\nYou architect.");
        WriteFile(src, "agents/code-reviewer.md", "---\nname: code-reviewer\ndescription: Reviews PRs.\n---\nYou review.");
        WriteFile(src, "skills/tdd/SKILL.md", "---\nname: tdd\ndescription: Use when implementing.\n---\n# TDD\nWrite the failing test first.");
        WriteFile(src, "skills/systematic-debugging/SKILL.md", "---\nname: systematic-debugging\ndescription: Use when stuck.\n---\n# Debug");
        WriteFile(src, "agents/dup-a.md", "---\nname: duplicate-agent\ndescription: First copy.\n---\nA.");   // dup-a + dup-b both derive
        WriteFile(src, "agents/dup-b.md", "---\nname: Duplicate Agent\ndescription: Second copy.\n---\nB.");  // the slug "duplicate-agent"
        WriteFile(src, "agents/skilled.md", "---\nname: skilled\ndescription: Carries skills.\nskills:\n  - tdd\n  - systematic-debugging\n  - ghost-skill\n---\nYou are skilled.");
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

    private async Task SeedAgentAsync(Guid teamId, Guid userId, string slug)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.AgentDefinition.Add(new AgentDefinition { Id = Guid.NewGuid(), TeamId = teamId, Slug = slug, Name = slug, Origin = AgentDefinitionOrigin.Authored, CreatedDate = DateTimeOffset.UtcNow, CreatedBy = userId, LastModifiedDate = DateTimeOffset.UtcNow, LastModifiedBy = userId });
        await db.SaveChangesAsync();
    }

    private async Task SeedSkillAsync(Guid teamId, Guid userId, string slug)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.SkillDefinition.Add(new SkillDefinition { Id = Guid.NewGuid(), TeamId = teamId, Slug = slug, Name = slug, Origin = SkillDefinitionOrigin.Authored, CreatedDate = DateTimeOffset.UtcNow, CreatedBy = userId, LastModifiedDate = DateTimeOffset.UtcNow, LastModifiedBy = userId });
        await db.SaveChangesAsync();
    }

    private async Task<(Guid TeamId, Guid UserId)> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"commit-{userId:N}@test.local", Name = $"commit-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"commit-{teamId:N}", Name = "Commit Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return (teamId, userId);
    }
}
