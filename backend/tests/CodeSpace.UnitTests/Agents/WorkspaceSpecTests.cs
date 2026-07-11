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
    public void PinnedSha_round_trips_the_related_repositories_projection_shape()
    {
        // S1: a pin dropped in the serialize→parse round-trip would be the silent tip fallback the pin forbids.
        var related = new[] { new WorkspaceRepositorySpec { Alias = "api", RepositoryId = Guid.NewGuid(), Access = WorkspaceAccess.Write, PinnedSha = "abc123def456" } };

        var serialized = System.Text.Json.JsonSerializer.SerializeToElement(Core.Services.Agents.Workspace.AgentWorkspaceAuthoring.SerializeRelatedRepositories(related));
        var parsed = Core.Services.Agents.Workspace.AgentWorkspaceAuthoring.ParseRelatedRepositories(serialized);

        parsed.Single().PinnedSha.ShouldBe("abc123def456");
    }

    [Fact]
    public void A_null_pin_is_omitted_from_the_serialized_related_shape_byte_identical()
    {
        var related = new[] { new WorkspaceRepositorySpec { Alias = "api", RepositoryId = Guid.NewGuid(), Access = WorkspaceAccess.Write } };

        var entry = Core.Services.Agents.Workspace.AgentWorkspaceAuthoring.SerializeRelatedRepositories(related)!.Single();

        entry.ContainsKey("pinnedSha").ShouldBeFalse("no pin ⇒ no key ⇒ the persisted shape stays byte-identical to before the field existed");
    }

    [Theory]
    [InlineData("abc123", "abc123")]                 // short id OK
    [InlineData("  ABC123DEF  ", "abc123def")]       // trimmed + lowercased
    public void ValidatePinnedSha_normalizes_valid_ids(string raw, string expected)
    {
        Core.Services.Agents.Workspace.RepositoryWorkspaceResolver.ValidatePinnedSha(raw).ShouldBe(expected);
    }

    [Theory]
    [InlineData("--pathspec-from-file=x")]   // flag-shaped garbage must never reach the git argv
    [InlineData("main")]                      // a branch NAME is not a pin — the pin's contract is an exact commit
    [InlineData("abc")]                       // too short to be a commit id
    public void ValidatePinnedSha_rejects_non_commit_ids_loud(string raw)
    {
        Should.Throw<Core.Services.Agents.Workspace.WorkspaceException>(() => Core.Services.Agents.Workspace.RepositoryWorkspaceResolver.ValidatePinnedSha(raw));
    }

    // ── S1: the launch vector's pins thread onto the workspace-spec factories. ──────────

    [Fact]
    public void FromRepository_carries_the_primary_pin()
    {
        var spec = WorkspaceSpec.FromRepository(Guid.NewGuid(), @ref: "main", pinnedSha: "abc123def456");

        spec.Repositories.Single().PinnedSha.ShouldBe("abc123def456");
    }

    [Fact]
    public void FromAuthoredRepos_carries_the_primary_pin_and_leaves_related_specs_as_authored()
    {
        var related = new[] { new WorkspaceRepositorySpec { Alias = "api", RepositoryId = Guid.NewGuid(), Access = WorkspaceAccess.Read, PinnedSha = "bbb222bbb222" } };

        var spec = WorkspaceSpec.FromAuthoredRepos(Guid.NewGuid(), primaryRef: null, related, primaryPinnedSha: "aaa111aaa111")!;

        spec.Repositories.Single(r => r.IsPrimary).PinnedSha.ShouldBe("aaa111aaa111");
        spec.Repositories.Single(r => !r.IsPrimary).PinnedSha.ShouldBe("bbb222bbb222", "a related spec's own pin survives the projection untouched");
    }

    [Fact]
    public void ResolveAuthoredWorkspace_builds_an_explicit_single_repo_spec_for_a_pin_only_run()
    {
        // A pin with no authored ref must still produce an explicit spec — a null Workspace would silently drop the
        // pin when the executor derives FromRepository(id) at the default branch's TIP.
        var spec = Core.Services.Agents.Workspace.AgentWorkspaceAuthoring.ResolveAuthoredWorkspace(Guid.NewGuid(), Array.Empty<WorkspaceRepositorySpec>(), primaryRef: null, primaryPinnedSha: "abc123def456");

        spec.ShouldNotBeNull();
        spec!.Repositories.Single().PinnedSha.ShouldBe("abc123def456");
    }

    [Fact]
    public void ResolveAuthoredWorkspace_stays_null_with_no_ref_and_no_pin_byte_identical()
    {
        Core.Services.Agents.Workspace.AgentWorkspaceAuthoring.ResolveAuthoredWorkspace(Guid.NewGuid(), Array.Empty<WorkspaceRepositorySpec>())
            .ShouldBeNull("no related repos, no ref, no pin ⇒ null Workspace ⇒ the executor's legacy single-repo derivation");
    }

    [Fact]
    public void SerializeRelatedRepositories_prefers_the_launch_vectors_pin_over_the_spec_carried_pin()
    {
        var repoId = Guid.NewGuid();
        var related = new[] { new WorkspaceRepositorySpec { Alias = "api", RepositoryId = repoId, Access = WorkspaceAccess.Write, PinnedSha = "0ld000000000" } };

        var entry = Core.Services.Agents.Workspace.AgentWorkspaceAuthoring.SerializeRelatedRepositories(related, pinnedShas: new Dictionary<Guid, string> { [repoId] = "fresh1234567" })!.Single();

        entry["pinnedSha"].ShouldBe("fresh1234567", "the launch vector is resolved NOW — a spec-carried pin authored earlier must not shadow it");
    }

    [Fact]
    public void SerializeRelatedRepositories_falls_back_to_the_spec_carried_pin_when_the_vector_lacks_the_repo()
    {
        var related = new[] { new WorkspaceRepositorySpec { Alias = "api", RepositoryId = Guid.NewGuid(), Access = WorkspaceAccess.Write, PinnedSha = "abc123def456" } };

        var entry = Core.Services.Agents.Workspace.AgentWorkspaceAuthoring.SerializeRelatedRepositories(related, pinnedShas: new Dictionary<Guid, string>())!.Single();

        entry["pinnedSha"].ShouldBe("abc123def456", "the spec's own round-tripped pin survives when the vector has no entry for the repo");
    }

    [Fact]
    public void FromAuthoredRepos_returns_null_with_no_related_repos_so_single_repo_stays_byte_identical()
    {
        WorkspaceSpec.FromAuthoredRepos(Guid.NewGuid(), primaryRef: null, Array.Empty<WorkspaceRepositorySpec>())
            .ShouldBeNull("no related repos → null Workspace → the resolver derives the single-repo workspace");
    }

    [Fact]
    public void FromAuthoredRepos_defaults_cwd_mode_to_auto_and_threads_an_explicit_one()
    {
        var related = new[] { new WorkspaceRepositorySpec { Alias = "api", RepositoryId = Guid.NewGuid(), Access = WorkspaceAccess.Read } };

        WorkspaceSpec.FromAuthoredRepos(Guid.NewGuid(), null, related)!.CwdMode.ShouldBe(WorkspaceCwdMode.Auto, "absent ⇒ Auto (byte-identical)");
        WorkspaceSpec.FromAuthoredRepos(Guid.NewGuid(), null, related, cwdMode: WorkspaceCwdMode.WorkspaceRoot)!.CwdMode.ShouldBe(WorkspaceCwdMode.WorkspaceRoot);
        WorkspaceSpec.FromAuthoredRepos(Guid.NewGuid(), null, related, cwdMode: WorkspaceCwdMode.PrimaryRepo)!.CwdMode.ShouldBe(WorkspaceCwdMode.PrimaryRepo);
    }

    [Theory]
    [InlineData("workspace", WorkspaceCwdMode.WorkspaceRoot)]
    [InlineData("WorkspaceRoot", WorkspaceCwdMode.WorkspaceRoot)]
    [InlineData("primary", WorkspaceCwdMode.PrimaryRepo)]
    [InlineData("PrimaryRepo", WorkspaceCwdMode.PrimaryRepo)]
    public void WorkspaceCwdModeWire_parses_both_the_modal_and_enum_vocabularies(string wire, WorkspaceCwdMode expected)
    {
        WorkspaceCwdModeWire.FromWire(wire).ShouldBe(expected);
        WorkspaceCwdModeWire.FromWire(wire.ToUpperInvariant()).ShouldBe(expected, "case-insensitive");
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("Auto")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("bogus")]
    public void WorkspaceCwdModeWire_folds_auto_blank_and_unknown_to_null_so_the_default_is_omitted(string? wire)
    {
        WorkspaceCwdModeWire.FromWire(wire).ShouldBeNull("auto / blank / unknown ⇒ null ⇒ the Auto default, omitted (byte-identical)");
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

    [Fact]
    public void FromAuthoredRepos_keeps_aliases_unique_when_an_authored_alias_collides_with_a_generated_fallback()
    {
        // The adversarial case: an authored "repo-3" must not collide with the repo-N a LATER blank repo falls back to.
        // The fallback loops past taken, so the blank one gets repo-2 (the next free), not a duplicate repo-3.
        var spec = WorkspaceSpec.FromAuthoredRepos(Guid.NewGuid(), null, new[]
        {
            new WorkspaceRepositorySpec { Alias = "repo-3", RepositoryId = Guid.NewGuid() },
            new WorkspaceRepositorySpec { Alias = "", RepositoryId = Guid.NewGuid() },
        })!;

        var aliases = spec.Repositories.Select(r => r.Alias).ToList();
        aliases.ShouldContain("repo-3");
        aliases.Distinct().Count().ShouldBe(aliases.Count, "no two repos share an alias — else the provider refuses the whole workspace at clone time");
    }

    [Theory]
    [InlineData("../etc")]   // traversal
    [InlineData("a/b")]      // path separator
    [InlineData(".")]        // single dot
    [InlineData("..")]       // double dot
    public void FromAuthoredRepos_replaces_an_unsafe_authored_alias_with_a_safe_fallback(string unsafeAlias)
    {
        // An unsafe authored alias must NEVER reach the clone as a mount segment — the factory falls back to repo-N so
        // the spec is safe BY CONSTRUCTION (the provider's IsSafeMountSegment stays as defence-in-depth).
        var spec = WorkspaceSpec.FromAuthoredRepos(Guid.NewGuid(), null, new[]
        {
            new WorkspaceRepositorySpec { Alias = unsafeAlias, RepositoryId = Guid.NewGuid() },
        })!;

        var related = spec.Repositories.Single(r => !r.IsPrimary);
        related.Alias.ShouldBe("repo-2", $"the unsafe alias '{unsafeAlias}' is replaced with a safe repo-N");
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
