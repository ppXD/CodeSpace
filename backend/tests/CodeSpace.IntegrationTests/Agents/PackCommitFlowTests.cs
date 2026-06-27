using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// The COMMIT half of the URL pack flow end-to-end through the REAL <see cref="PackImportService"/> + a real git
/// clone of a LOCAL repo (no network): committing a selection persists agents AND skills under a resolved
/// <c>Pack</c>, a re-sync UPSERTS on (pack, source-path) — same ids, no duplicates — and a handle that collides
/// with a pre-existing definition is Skipped while an unknown path Fails. Proves the idempotent sync identity.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class PackCommitFlowTests
{
    private readonly PostgresFixture _fixture;

    public PackCommitFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Commit_persists_agents_and_skills_then_a_resync_upserts_without_duplicating()
    {
        if (OperatingSystem.IsWindows()) return;

        using var seedScope = _fixture.BeginScope();
        if (!await GitReadyAsync(seedScope.Resolve<ISandboxRunnerRegistry>())) return;

        var (teamId, userId) = await SeedTeamAsync();
        var src = await CreateSourceRepoAsync(seedScope.Resolve<ISandboxRunnerRegistry>());
        var paths = new[] { "agents/backend-architect.md", "skills/tdd/SKILL.md" };

        try
        {
            var first = await CommitAsync(src, paths, teamId, userId);

            first.PackId.ShouldNotBe(Guid.Empty);
            first.Items.ShouldAllBe(i => i.Outcome == PackImportOutcome.Imported);

            // The pack + both rows landed, pack-rooted, with content + provenance.
            await AssertStateAsync(teamId, async db =>
            {
                (await db.Pack.CountAsync(p => p.TeamId == teamId && p.DeletedDate == null)).ShouldBe(1);

                var agent = await db.AgentDefinition.SingleAsync(a => a.TeamId == teamId && a.Slug == "backend-architect" && a.DeletedDate == null);
                agent.Origin.ShouldBe(AgentDefinitionOrigin.Imported);
                agent.PackId.ShouldBe(first.PackId);
                agent.SourcePath.ShouldBe("agents/backend-architect.md");
                agent.SystemPrompt.ShouldContain("You architect");

                var skill = await db.SkillDefinition.SingleAsync(s => s.TeamId == teamId && s.Slug == "tdd" && s.DeletedDate == null);
                skill.Origin.ShouldBe(SkillDefinitionOrigin.Imported);
                skill.PackId.ShouldBe(first.PackId);
                skill.Body.ShouldContain("# TDD");
            });

            var agentId = first.Items.Single(i => i.Kind == PackArtifactKind.Agent).DefinitionId;
            var skillId = first.Items.Single(i => i.Kind == PackArtifactKind.Skill).DefinitionId;

            // Re-sync: same pack, every item Updated (not re-Imported), and the row ids are unchanged.
            var second = await CommitAsync(src, paths, teamId, userId);

            second.PackId.ShouldBe(first.PackId, "the same source resolves to the same pack");
            second.Items.ShouldAllBe(i => i.Outcome == PackImportOutcome.Updated);
            second.Items.Single(i => i.Kind == PackArtifactKind.Agent).DefinitionId.ShouldBe(agentId);
            second.Items.Single(i => i.Kind == PackArtifactKind.Skill).DefinitionId.ShouldBe(skillId);

            // No duplication: still exactly one pack and one row per (pack, source).
            await AssertStateAsync(teamId, async db =>
            {
                (await db.Pack.CountAsync(p => p.TeamId == teamId && p.DeletedDate == null)).ShouldBe(1);
                (await db.AgentDefinition.CountAsync(a => a.TeamId == teamId && a.PackId == first.PackId && a.DeletedDate == null)).ShouldBe(1);
                (await db.SkillDefinition.CountAsync(s => s.TeamId == teamId && s.PackId == first.PackId && s.DeletedDate == null)).ShouldBe(1);
            });
        }
        finally
        {
            try { Directory.Delete(src, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Commit_skips_a_handle_conflict_and_fails_an_unknown_path()
    {
        if (OperatingSystem.IsWindows()) return;

        using var seedScope = _fixture.BeginScope();
        if (!await GitReadyAsync(seedScope.Resolve<ISandboxRunnerRegistry>())) return;

        var (teamId, userId) = await SeedTeamAsync();
        await SeedAgentAsync(teamId, userId, "backend-architect");   // an authored persona the pack agent collides with
        var src = await CreateSourceRepoAsync(seedScope.Resolve<ISandboxRunnerRegistry>());

        try
        {
            var result = await CommitAsync(src, new[] { "agents/backend-architect.md", "agents/does-not-exist.md" }, teamId, userId);

            var conflict = result.Items.Single(i => i.SourcePath == "agents/backend-architect.md");
            conflict.Outcome.ShouldBe(PackImportOutcome.Skipped);
            conflict.Kind.ShouldBe(PackArtifactKind.Agent);
            conflict.Reason.ShouldNotBeNullOrWhiteSpace();

            var missing = result.Items.Single(i => i.SourcePath == "agents/does-not-exist.md");
            missing.Outcome.ShouldBe(PackImportOutcome.Failed);
            missing.Kind.ShouldBeNull();

            // The pre-existing authored persona is untouched (still authored, no pack), and no second row was created.
            await AssertStateAsync(teamId, async db =>
            {
                var agent = await db.AgentDefinition.SingleAsync(a => a.TeamId == teamId && a.Slug == "backend-architect" && a.DeletedDate == null);
                agent.Origin.ShouldBe(AgentDefinitionOrigin.Authored);
                agent.PackId.ShouldBeNull();
            });
        }
        finally
        {
            try { Directory.Delete(src, recursive: true); } catch { /* best-effort */ }
        }
    }

    private async Task<PackImportResult> CommitAsync(string url, IReadOnlyList<string> paths, Guid teamId, Guid actorUserId)
    {
        using var scope = _fixture.BeginScope();
        var service = new PackImportService(
            new PackCloneFetcher(new AllowAll(), scope.Resolve<ISandboxRunnerRegistry>(), NullLogger<PackCloneFetcher>.Instance),
            scope.Resolve<IPackSourceWalker>(),
            scope.Resolve<CodeSpaceDbContext>());

        return await service.ImportFromUrlAsync(url, reference: null, paths, teamId, actorUserId, CancellationToken.None);
    }

    private async Task AssertStateAsync(Guid teamId, Func<CodeSpaceDbContext, Task> assert)
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
        WriteFile(src, "skills/tdd/SKILL.md", "---\nname: tdd\ndescription: Use when implementing.\n---\n# TDD\nWrite the failing test first.");
        await RunGitAsync(runners, new[] { "-C", src, "-c", "user.email=t@codespace.test", "-c", "user.name=Test", "add", "-A" });
        await RunGitAsync(runners, new[] { "-C", src, "-c", "user.email=t@codespace.test", "-c", "user.name=Test", "commit", "-m", "init" });
        return src;
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
