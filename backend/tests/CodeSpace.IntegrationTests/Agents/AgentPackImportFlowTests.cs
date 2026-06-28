using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Repositories;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// The pack preview→import flow against real Postgres + the REAL parser + the REAL import writer, with a
/// FAKE <see cref="IRepositorySourceService"/> standing in for the provider fetch (no network/auth needed —
/// the orchestration, parsing, slug-conflict detection, and persistence are all real). Proves: discovery
/// filters to *.md files; the full structure is parsed into the preview; slug conflicts are flagged + skipped;
/// commit persists only the selected importable rows as Origin=Imported.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AgentPackImportFlowTests
{
    private readonly PostgresFixture _fixture;

    public AgentPackImportFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    private static readonly IReadOnlyList<RemoteTreeEntry> Tree = new[]
    {
        Entry("agents/backend-architect.md", RemoteTreeEntryType.File),
        Entry("agents/reviewer.md", RemoteTreeEntryType.File),
        Entry("agents/README.md", RemoteTreeEntryType.File),       // .md but no frontmatter → un-importable
        Entry("agents/helpers", RemoteTreeEntryType.Directory),    // filtered out (not a file)
    };

    private static readonly IReadOnlyDictionary<string, string?> Files = new Dictionary<string, string?>
    {
        ["agents/backend-architect.md"] = "---\nname: backend-architect\nmodel: claude-opus-4-8\ntools: Read, Grep\n---\nYou are a senior architect.\n",
        ["agents/reviewer.md"] = "---\nname: reviewer\n---\nReview the code.\n",
        ["agents/README.md"] = "# Agents\nThis folder holds agent personas.\n",   // no frontmatter
    };

    [Fact]
    public async Task Preview_discovers_md_files_and_parses_their_full_structure()
    {
        var teamId = await SeedTeamAsync();
        var svc = BuildService(out var scope);
        using (scope)
        {
            var preview = await svc.PreviewAsync(Guid.NewGuid(), reference: null, rootPath: null, teamId, CancellationToken.None);

            preview.RootPath.ShouldBe("agents");
            preview.Items.Select(i => i.SourcePath).ShouldBe(new[] { "agents/backend-architect.md", "agents/reviewer.md", "agents/README.md" },
                customMessage: "only *.md FILES are discovered — the directory is filtered out");

            var arch = preview.Items.Single(i => i.SourcePath == "agents/backend-architect.md");
            arch.Name.ShouldBe("backend-architect");
            arch.DerivedSlug.ShouldBe("backend-architect");
            arch.Model.ShouldBe("claude-opus-4-8");
            arch.Tools.ShouldBe(new[] { "Read", "Grep" });
            arch.Importable.ShouldBeTrue();
            arch.SlugConflict.ShouldBeFalse();

            var readme = preview.Items.Single(i => i.SourcePath == "agents/README.md");
            readme.Importable.ShouldBeFalse("a file with no frontmatter / name can't be imported");
            readme.Diagnostics.ShouldNotBeEmpty();
        }
    }

    [Fact]
    public async Task Preview_flags_a_handle_that_already_exists_in_the_team()
    {
        var teamId = await SeedTeamAsync();
        await SeedPersonaAsync(teamId, "reviewer");   // an existing active persona with this handle

        var svc = BuildService(out var scope);
        using (scope)
        {
            var preview = await svc.PreviewAsync(Guid.NewGuid(), null, null, teamId, CancellationToken.None);

            var reviewer = preview.Items.Single(i => i.SourcePath == "agents/reviewer.md");
            reviewer.SlugConflict.ShouldBeTrue("an active persona already owns the 'reviewer' handle");
            reviewer.Importable.ShouldBeFalse("a conflicting handle is not importable — import would skip it");
        }
    }

    [Fact]
    public async Task Preview_does_not_flag_a_handle_owned_only_by_a_store_snapshot()
    {
        var teamId = await SeedTeamAsync();
        await SeedPersonaAsync(teamId, "reviewer", DefinitionScope.Store);   // only a Library STORE snapshot owns the handle

        var svc = BuildService(out var scope);
        using (scope)
        {
            var preview = await svc.PreviewAsync(Guid.NewGuid(), null, null, teamId, CancellationToken.None);

            // A store snapshot carries no unique handle and the import write checks only Working rows, so this must
            // NOT over-report a conflict — the agent is importable.
            var reviewer = preview.Items.Single(i => i.SourcePath == "agents/reviewer.md");
            reviewer.SlugConflict.ShouldBeFalse("a store snapshot does not own a unique handle, so it must not flag a conflict");
            reviewer.Importable.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task Import_persists_the_selected_agents_as_imported_personas()
    {
        var teamId = await SeedTeamAsync();
        var userId = await OwnerOfAsync(teamId);
        var svc = BuildService(out var scope);

        IReadOnlyList<Messages.Agents.AgentImportResult> results;
        using (scope)
            results = await svc.ImportAsync(Guid.NewGuid(), null, null,
                new[] { "agents/backend-architect.md", "agents/reviewer.md" }, teamId, userId, CancellationToken.None);

        results.Count.ShouldBe(2);
        results.ShouldAllBe(r => r.Outcome == AgentImportOutcome.Imported);

        using var verify = _fixture.BeginScope();
        var rows = await verify.Resolve<CodeSpaceDbContext>().AgentDefinition.AsNoTracking()
            .Where(a => a.TeamId == teamId).ToListAsync();

        rows.Select(r => r.Slug).OrderBy(s => s).ShouldBe(new[] { "backend-architect", "reviewer" });
        rows.ShouldAllBe(r => r.Origin == AgentDefinitionOrigin.Imported);
        rows.Single(r => r.Slug == "backend-architect").SourcePath.ShouldBe("agents/backend-architect.md");
    }

    [Fact]
    public async Task Import_skips_a_handle_collision_and_fails_an_unparseable_file()
    {
        var teamId = await SeedTeamAsync();
        var userId = await OwnerOfAsync(teamId);
        await SeedPersonaAsync(teamId, "reviewer");   // pre-existing → reviewer.md collides

        var svc = BuildService(out var scope);
        IReadOnlyList<Messages.Agents.AgentImportResult> results;
        using (scope)
            results = await svc.ImportAsync(Guid.NewGuid(), null, null,
                new[] { "agents/reviewer.md", "agents/README.md" }, teamId, userId, CancellationToken.None);

        results.Single(r => r.SourcePath == "agents/reviewer.md").Outcome.ShouldBe(AgentImportOutcome.Skipped, "the handle already exists — never overwrite it");
        results.Single(r => r.SourcePath == "agents/README.md").Outcome.ShouldBe(AgentImportOutcome.Failed, "no name → can't import");

        // The pre-existing persona is untouched (still exactly one 'reviewer').
        using var verify = _fixture.BeginScope();
        (await verify.Resolve<CodeSpaceDbContext>().AgentDefinition.AsNoTracking().CountAsync(a => a.TeamId == teamId && a.Slug == "reviewer")).ShouldBe(1);
    }

    private AgentPackImportService BuildService(out ILifetimeScope scope)
    {
        scope = _fixture.BeginScope();
        return new AgentPackImportService(
            new FakeSource(Tree, Files),
            scope.Resolve<IAgentArtifactParserRegistry>(),
            scope.Resolve<IAgentDefinitionService>(),
            scope.Resolve<CodeSpaceDbContext>());
    }

    private static RemoteTreeEntry Entry(string path, RemoteTreeEntryType type) =>
        new() { Name = path.Split('/')[^1], Path = path, Type = type };

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"pack-{userId:N}@test.local", Name = $"pack-{userId:N}", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Team.Add(new Team { Id = teamId, Slug = $"pack-team-{teamId:N}", Name = "Pack Team", Kind = TeamKind.Workspace, OwnerUserId = userId, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        await db.SaveChangesAsync();
        return teamId;
    }

    private async Task<Guid> OwnerOfAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return (await scope.Resolve<CodeSpaceDbContext>().Team.AsNoTracking().SingleAsync(t => t.Id == teamId)).OwnerUserId;
    }

    private async Task SeedPersonaAsync(Guid teamId, string slug, DefinitionScope scope = DefinitionScope.Working)
    {
        using var s = _fixture.BeginScope();
        var db = s.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.AgentDefinition.Add(new AgentDefinition
        {
            Id = Guid.NewGuid(), TeamId = teamId, Slug = slug, Name = slug,
            Origin = scope == DefinitionScope.Store ? AgentDefinitionOrigin.Imported : AgentDefinitionOrigin.Authored, Scope = scope,
            CreatedDate = now, CreatedBy = SystemUsers.SeederId, LastModifiedDate = now, LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync();
    }

    private sealed class FakeSource : IRepositorySourceService
    {
        private readonly IReadOnlyList<RemoteTreeEntry> _tree;
        private readonly IReadOnlyDictionary<string, string?> _files;

        public FakeSource(IReadOnlyList<RemoteTreeEntry> tree, IReadOnlyDictionary<string, string?> files) { _tree = tree; _files = files; }

        public Task<IReadOnlyList<RemoteBranch>> ListBranchesAsync(Guid repositoryId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RemoteBranch>>(Array.Empty<RemoteBranch>());

        public Task<IReadOnlyList<RemoteTreeEntry>> ListTreeAsync(Guid repositoryId, string? path, string? reference, CancellationToken cancellationToken) =>
            Task.FromResult(_tree);

        public Task<RemoteFileContent> GetFileAsync(Guid repositoryId, string path, string? reference, CancellationToken cancellationToken)
        {
            var text = _files.TryGetValue(path, out var t) ? t : null;
            return Task.FromResult(new RemoteFileContent { Path = path, Name = path.Split('/')[^1], Size = text?.Length ?? 0, IsBinary = text is null, Text = text });
        }
    }
}
