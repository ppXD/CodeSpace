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

    [Fact]
    public void ResolveAsync_refuses_a_multi_repo_workspace_in_slice_1()
    {
        var resolver = new RepositoryWorkspaceResolver(db: null!, auth: null!);
        var task = new AgentTask
        {
            Goal = "g",
            Harness = "claude-code",
            Workspace = new WorkspaceSpec { Repositories = new[] { Repo("web", WorkspaceAccess.Write, isPrimary: true), Repo("api", WorkspaceAccess.Write) } },
        };

        // The Count > 1 guard throws BEFORE touching the (null) DB — a premature multi-repo run fails loud,
        // never silently drops a repo. Single-repo resolution (which hits the DB) is covered at the integration tier.
        Should.Throw<WorkspaceException>(() => resolver.ResolveAsync(task, Guid.NewGuid(), CancellationToken.None))
            .Message.ShouldContain("Multi-repo");
    }

    private static WorkspaceRepositorySpec Repo(string alias, WorkspaceAccess access, bool isPrimary = false) =>
        new() { Alias = alias, RepositoryId = Guid.NewGuid(), Path = alias, Access = access, IsPrimary = isPrimary };
}
