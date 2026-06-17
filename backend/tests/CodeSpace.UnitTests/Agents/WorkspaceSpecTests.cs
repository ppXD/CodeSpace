using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the multi-repo PR1 workspace data model + the resolver's canonicalization/guard, WITHOUT a DB. Pins
/// the back-compat bridge (a legacy <c>RepositoryId</c> derives the SAME single-repo <see cref="WorkspaceSpec"/>),
/// the primary-repo resolution precedence, and the slice-1 invariants the resolver enforces BEFORE any DB call:
/// a no-repo run resolves to null, and a multi-repo workspace is REFUSED (not yet executable) rather than silently
/// dropping repos. The single-repo byte-identical RESOLUTION is proven at the integration tier (existing agent
/// suites stay green).
/// </summary>
[Trait("Category", "Unit")]
public class WorkspaceSpecTests
{
    // ── WorkspaceSpec.FromRepository: the legacy single-repo shape ────────────────────

    [Fact]
    public void FromRepository_builds_one_writable_primary_repo_at_the_default_alias()
    {
        var repoId = Guid.NewGuid();

        var spec = WorkspaceSpec.FromRepository(repoId);

        spec.Repositories.Count.ShouldBe(1);
        var only = spec.Repositories[0];
        only.RepositoryId.ShouldBe(repoId);
        only.Alias.ShouldBe(WorkspaceSpec.DefaultAlias);
        only.Access.ShouldBe(WorkspaceAccess.Write, "the legacy single repo is writable");
        only.IsPrimary.ShouldBeTrue();
        only.Ref.ShouldBeNull("null ref → the resolver uses the repo's default branch (legacy behaviour)");
        spec.PrimaryAlias.ShouldBe(WorkspaceSpec.DefaultAlias);
        spec.CwdMode.ShouldBe(WorkspaceCwdMode.Auto);
    }

    [Fact]
    public void FromRepository_threads_an_explicit_ref()
    {
        WorkspaceSpec.FromRepository(Guid.NewGuid(), "release/1.2").Repositories[0].Ref.ShouldBe("release/1.2");
    }

    // ── WorkspaceSpec.FromAuthoredRepos: the multi-repo authoring projection ──────────

    [Fact]
    public void FromAuthoredRepos_returns_null_with_no_related_repos_so_single_repo_stays_byte_identical()
    {
        WorkspaceSpec.FromAuthoredRepos(Guid.NewGuid(), primaryRef: null, Array.Empty<WorkspaceRepositorySpec>())
            .ShouldBeNull("no related repos → null Workspace → the resolver derives the single-repo workspace");
    }

    [Fact]
    public void FromAuthoredRepos_assembles_the_primary_plus_related()
    {
        var web = Guid.NewGuid();
        var api = Guid.NewGuid();

        var spec = WorkspaceSpec.FromAuthoredRepos(web, "main", new[]
        {
            new WorkspaceRepositorySpec { Alias = "api", RepositoryId = api, Access = WorkspaceAccess.Read },
        })!;

        spec.Repositories.Count.ShouldBe(2);
        spec.PrimaryAlias.ShouldBe(WorkspaceSpec.DefaultAlias);

        var primary = spec.Repositories.Single(r => r.IsPrimary);
        primary.Alias.ShouldBe(WorkspaceSpec.DefaultAlias, "the primary keeps the legacy default alias for byte-identical cwd");
        primary.RepositoryId.ShouldBe(web);
        primary.Ref.ShouldBe("main");
        primary.Access.ShouldBe(WorkspaceAccess.Write);

        var related = spec.Repositories.Single(r => !r.IsPrimary);
        related.Alias.ShouldBe("api");
        related.RepositoryId.ShouldBe(api);
        related.Access.ShouldBe(WorkspaceAccess.Read);
    }

    [Fact]
    public void FromAuthoredRepos_normalizes_a_blank_or_colliding_alias_to_a_stable_unique_one()
    {
        var spec = WorkspaceSpec.FromAuthoredRepos(Guid.NewGuid(), null, new[]
        {
            new WorkspaceRepositorySpec { Alias = "", RepositoryId = Guid.NewGuid(), Access = WorkspaceAccess.Read },                         // blank → repo-2
            new WorkspaceRepositorySpec { Alias = WorkspaceSpec.DefaultAlias, RepositoryId = Guid.NewGuid(), Access = WorkspaceAccess.Read }, // collides with primary "repo" → repo-3
        })!;

        var aliases = spec.Repositories.Select(r => r.Alias).ToList();
        aliases.ShouldBe(new[] { WorkspaceSpec.DefaultAlias, "repo-2", "repo-3" });
        aliases.Distinct().Count().ShouldBe(3, "every alias is unique (the provider's mount-layout guard needs it)");
    }

    // ── WorkspaceSpec.Primary: resolution precedence ─────────────────────────────────

    [Fact]
    public void Primary_prefers_the_explicit_primary_alias()
    {
        var spec = new WorkspaceSpec
        {
            PrimaryAlias = "api",
            Repositories = new[]
            {
                Repo("web", WorkspaceAccess.Write, isPrimary: true),
                Repo("api", WorkspaceAccess.Write),
            },
        };

        spec.Primary!.Alias.ShouldBe("api", "PrimaryAlias wins over the IsPrimary flag");
    }

    [Fact]
    public void Primary_falls_back_to_the_isPrimary_flag_then_first_writable_then_first()
    {
        // No PrimaryAlias → the IsPrimary repo.
        new WorkspaceSpec { Repositories = new[] { Repo("a", WorkspaceAccess.Read), Repo("b", WorkspaceAccess.Write, isPrimary: true) } }
            .Primary!.Alias.ShouldBe("b");

        // No PrimaryAlias, no IsPrimary → the first WRITABLE (skips the read-only context repo).
        new WorkspaceSpec { Repositories = new[] { Repo("ctx", WorkspaceAccess.Read), Repo("app", WorkspaceAccess.Write) } }
            .Primary!.Alias.ShouldBe("app");

        // All read-only → the first.
        new WorkspaceSpec { Repositories = new[] { Repo("x", WorkspaceAccess.Read), Repo("y", WorkspaceAccess.Read) } }
            .Primary!.Alias.ShouldBe("x");
    }

    [Fact]
    public void Primary_falls_through_when_the_PrimaryAlias_matches_no_repo()
    {
        // A stale/typo'd PrimaryAlias must not return null — it falls through to the IsPrimary/writable/first chain.
        new WorkspaceSpec
        {
            PrimaryAlias = "typo-does-not-exist",
            Repositories = new[] { Repo("api", WorkspaceAccess.Read), Repo("web", WorkspaceAccess.Write) },
        }.Primary!.Alias.ShouldBe("web", "an unmatched PrimaryAlias falls through to the first writable repo, not null");
    }

    // ── Resolver canonicalization (pure) ─────────────────────────────────────────────

    [Fact]
    public void CanonicalWorkspace_prefers_the_authored_spec_over_the_legacy_repository_id()
    {
        var authored = WorkspaceSpec.FromRepository(Guid.NewGuid());
        var task = new AgentTask { Goal = "g", Harness = "claude-code", RepositoryId = Guid.NewGuid(), Workspace = authored };

        RepositoryWorkspaceResolver.CanonicalWorkspace(task).ShouldBeSameAs(authored, "an authored Workspace is canonical; RepositoryId is ignored");
    }

    [Fact]
    public void CanonicalWorkspace_derives_a_single_repo_spec_from_the_legacy_repository_id()
    {
        var repoId = Guid.NewGuid();
        var task = new AgentTask { Goal = "g", Harness = "claude-code", RepositoryId = repoId };

        var spec = RepositoryWorkspaceResolver.CanonicalWorkspace(task);

        spec.ShouldNotBeNull();
        spec!.Repositories.Count.ShouldBe(1);
        spec.Repositories[0].RepositoryId.ShouldBe(repoId);
    }

    [Fact]
    public void CanonicalWorkspace_is_null_for_a_no_repo_run()
    {
        RepositoryWorkspaceResolver.CanonicalWorkspace(new AgentTask { Goal = "g", Harness = "claude-code" }).ShouldBeNull();
    }

    // ── Resolver guards (short-circuit BEFORE any DB call, so db-less) ────────────────

    [Fact]
    public async Task ResolveAsync_returns_null_for_a_no_repo_run()
    {
        var resolver = new RepositoryWorkspaceResolver(db: null!, auth: null!);

        (await resolver.ResolveAsync(new AgentTask { Goal = "g", Harness = "claude-code" }, Guid.NewGuid(), CancellationToken.None))
            .ShouldBeNull();
    }

    // (The slice-1 "multi-repo is refused" guard was intentionally LIFTED in the clone slice — multi-repo now
    //  resolves every repo into a provision, covered by RepositoryWorkspaceResolverTests at the integration tier.)

    [Fact]
    public async Task ResolveAsync_refuses_an_empty_repositories_spec_with_a_distinct_error()
    {
        var resolver = new RepositoryWorkspaceResolver(db: null!, auth: null!);
        var task = new AgentTask
        {
            Goal = "g",
            Harness = "claude-code",
            Workspace = new WorkspaceSpec { Repositories = Array.Empty<WorkspaceRepositorySpec>() },
        };

        // Count == 0 slips PAST the multi-repo (>1) guard, then Primary is null → the distinct no-repositories
        // throw (NOT the multi-repo message, and NOT an NPE on primary.RepositoryId). Also db-less.
        var ex = await Should.ThrowAsync<WorkspaceException>(() => resolver.ResolveAsync(task, Guid.NewGuid(), CancellationToken.None));
        ex.Message.ShouldContain("no repositories");
        ex.Message.ShouldNotContain("Multi-repo");
    }

    // ── AgentJson persistence round-trip (the resolver reads a DESERIALIZED Workspace at execution) ───

    [Fact]
    public void AgentTask_Workspace_round_trips_through_AgentJson()
    {
        // AgentRunService persists the task as task_jsonb via AgentJson.Options and the executor deserializes it
        // before resolving the workspace — so the spec (incl. the WorkspaceAccess / WorkspaceCwdMode enums) must
        // survive the canonical serialization, or the resolver reads a corrupted/empty Workspace at run time.
        var task = new AgentTask
        {
            Goal = "g",
            Harness = "claude-code",
            Workspace = new WorkspaceSpec
            {
                PrimaryAlias = "web",
                CwdMode = WorkspaceCwdMode.WorkspaceRoot,
                Repositories = new[]
                {
                    new WorkspaceRepositorySpec { Alias = "web", RepositoryId = Guid.NewGuid(), Ref = "main", Path = "web", Access = WorkspaceAccess.Write, IsPrimary = true },
                    Repo("api", WorkspaceAccess.Read),
                },
            },
        };

        var rehydrated = JsonSerializer.Deserialize<AgentTask>(JsonSerializer.Serialize(task, AgentJson.Options), AgentJson.Options)!;

        var ws = rehydrated.Workspace.ShouldNotBeNull();
        ws.PrimaryAlias.ShouldBe("web");
        ws.CwdMode.ShouldBe(WorkspaceCwdMode.WorkspaceRoot, "the cwd-mode enum survives the round-trip");
        ws.Repositories.Count.ShouldBe(2);
        ws.Repositories[0].RepositoryId.ShouldBe(task.Workspace!.Repositories[0].RepositoryId);
        ws.Repositories[0].Access.ShouldBe(WorkspaceAccess.Write, "the access enum survives the round-trip");
        ws.Repositories[1].Access.ShouldBe(WorkspaceAccess.Read);
        ws.Primary!.Alias.ShouldBe("web");
    }

    private static WorkspaceRepositorySpec Repo(string alias, WorkspaceAccess access, bool isPrimary = false) =>
        new() { Alias = alias, RepositoryId = Guid.NewGuid(), Path = alias, Access = access, IsPrimary = isPrimary };
}
