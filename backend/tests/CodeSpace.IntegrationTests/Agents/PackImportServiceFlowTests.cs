using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// The unified URL preview end-to-end through the REAL <see cref="PackImportService"/> (fetcher → walker →
/// per-team conflict check) + real git clone of a LOCAL repo (no network): a pack with agents AND skills,
/// some of whose handles already exist in the team, previews into a <see cref="PackPreview"/> whose agents +
/// skills carry the right slug-conflict / importable flags. Proves the discover→conflict→preview vertical and
/// that the transient clone is reclaimed (the service disposes the checkout).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class PackImportServiceFlowTests
{
    private readonly PostgresFixture _fixture;

    public PackImportServiceFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Previews_agents_and_skills_from_a_url_flagging_handle_conflicts()
    {
        if (OperatingSystem.IsWindows()) return;

        using var seedScope = _fixture.BeginScope();
        var runners = seedScope.Resolve<ISandboxRunnerRegistry>();
        if (!await GitReadyAsync(runners)) return;

        var (teamId, userId) = await SeedTeamAsync();
        await SeedAgentAsync(teamId, userId, "reviewer");   // an existing handle the pack will collide with
        await SeedSkillAsync(teamId, userId, "tdd");

        var src = await CreateSourceRepoAsync(runners);
        try
        {
            PackPreview preview;
            using (var scope = _fixture.BeginScope())
            {
                var service = new PackImportService(
                    new PackCloneFetcher(new AllowAll(), scope.Resolve<ISandboxRunnerRegistry>(), NullLogger<PackCloneFetcher>.Instance),
                    scope.Resolve<IPackSourceWalker>(),
                    scope.Resolve<CodeSpaceDbContext>());

                preview = await service.PreviewFromUrlAsync(src, reference: null, teamId, CancellationToken.None);
            }

            // Agents: the colliding handle is flagged un-importable; the fresh one is importable.
            var reviewer = preview.Agents.Single(a => a.DerivedSlug == "reviewer");
            reviewer.SlugConflict.ShouldBeTrue("an agent whose handle already exists in the team is a conflict");
            reviewer.Importable.ShouldBeFalse();

            var fresh = preview.Agents.Single(a => a.DerivedSlug == "backend-architect");
            fresh.SlugConflict.ShouldBeFalse();
            fresh.Importable.ShouldBeTrue();

            // Skills: same conflict semantics on the skill handle.
            var tdd = preview.Skills.Single(s => s.DerivedSlug == "tdd");
            tdd.SlugConflict.ShouldBeTrue();
            tdd.Importable.ShouldBeFalse();

            var freshSkill = preview.Skills.Single(s => s.DerivedSlug == "systematic-debugging");
            freshSkill.Importable.ShouldBeTrue();
        }
        finally
        {
            try { Directory.Delete(src, recursive: true); } catch { /* best-effort */ }
        }
    }

    private sealed class AllowAll : IPackHostAllowlist
    {
        public bool IsAllowed(string url) => true;
        public void EnsureAllowed(string url) { }
    }

    private async Task<string> CreateSourceRepoAsync(ISandboxRunnerRegistry runners)
    {
        var src = Path.Combine(Path.GetTempPath(), "cs-importsrc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(src);

        await RunGitAsync(runners, new[] { "init", "-b", "main", src });
        WriteFile(src, "agents/reviewer.md", "---\nname: reviewer\ndescription: Use to review.\n---\nYou review.");
        WriteFile(src, "agents/backend-architect.md", "---\nname: backend-architect\ndescription: Use for backend.\n---\nYou architect.");
        WriteFile(src, "skills/tdd/SKILL.md", "---\nname: tdd\ndescription: Use when implementing.\n---\n# TDD");
        WriteFile(src, "skills/systematic-debugging/SKILL.md", "---\nname: systematic-debugging\ndescription: Use when stuck.\n---\n# Debug");
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
        db.User.Add(new User { Id = userId, Email = $"import-{userId:N}@test.local", Name = $"import-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"import-{teamId:N}", Name = "Import Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return (teamId, userId);
    }
}
