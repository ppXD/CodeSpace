using System.Text.Json;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the SHARED multi-repo workspace authoring底層 (resolver loop #379, S7-A0) — the parse + resolve both the
/// <c>agent.code</c> node and the supervisor spawn funnel through, so there is ONE authored-repos → workspace
/// implementation (Rule 7) instead of a per-producer hand-mirror. These pin the exact lenient-parse + null-workspace
/// rules the agent.code node relied on (its <c>AgentCodeNodeTests</c> are the byte-identity guard that the relocation
/// preserved behaviour) AND the rules the supervisor (S7-A) will inherit for free.
/// </summary>
[Trait("Category", "Unit")]
public class AgentWorkspaceAuthoringTests
{
    private static readonly string RepoA = "11111111-1111-1111-1111-111111111111";
    private static readonly string RepoB = "22222222-2222-2222-2222-222222222222";

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    // ── ParseRelatedRepositories ─────────────────────────────────────────────────────

    [Fact]
    public void Parses_a_well_formed_entry_with_alias_and_write_access()
    {
        var related = AgentWorkspaceAuthoring.ParseRelatedRepositories(Json($$"""[{"repositoryId":"{{RepoA}}","alias":"api","access":"write"}]"""));

        related.Count.ShouldBe(1);
        related[0].RepositoryId.ShouldBe(Guid.Parse(RepoA));
        related[0].Alias.ShouldBe("api");
        related[0].Access.ShouldBe(WorkspaceAccess.Write);
    }

    [Fact]
    public void Defaults_a_missing_alias_to_blank_and_missing_access_to_read()
    {
        var related = AgentWorkspaceAuthoring.ParseRelatedRepositories(Json($$"""[{"repositoryId":"{{RepoA}}"}]"""));

        related.Count.ShouldBe(1);
        related[0].Alias.ShouldBe("", "a blank alias lets WorkspaceSpec.FromAuthoredRepos assign a unique one");
        related[0].Access.ShouldBe(WorkspaceAccess.Read, "the safe default is read-only context — a related repo is writable only when authored so");
    }

    [Theory]
    [InlineData("write", WorkspaceAccess.Write)]
    [InlineData("WRITE", WorkspaceAccess.Write)]   // case-insensitive
    [InlineData("Write", WorkspaceAccess.Write)]
    [InlineData("read", WorkspaceAccess.Read)]
    [InlineData("readonly", WorkspaceAccess.Read)]   // anything not "write" → read
    [InlineData("garbage", WorkspaceAccess.Read)]
    public void Maps_access_case_insensitively_defaulting_to_read(string access, WorkspaceAccess expected) =>
        AgentWorkspaceAuthoring.ParseRelatedRepositories(Json($$"""[{"repositoryId":"{{RepoA}}","access":"{{access}}"}]"""))[0].Access.ShouldBe(expected);

    [Fact]
    public void Trims_whitespace_around_the_alias() =>
        AgentWorkspaceAuthoring.ParseRelatedRepositories(Json($$"""[{"repositoryId":"{{RepoA}}","alias":"  api  "}]"""))[0].Alias.ShouldBe("api");

    [Fact]
    public void Parses_a_per_repo_ref_onto_the_spec_for_branch_continuity() =>
        AgentWorkspaceAuthoring.ParseRelatedRepositories(Json($$"""[{"repositoryId":"{{RepoA}}","alias":"api","ref":"run-1/api"}]"""))[0].Ref
            .ShouldBe("run-1/api", "the authored per-repo ref flows onto WorkspaceRepositorySpec.Ref — each related repo clones at its prior branch");

    [Fact]
    public void Parses_refSoftFallback_onto_the_spec_for_a_session_related_ref()
    {
        // A SESSION-inherited related ref carries refSoftFallback:true (set by SerializeRelatedRepositories) → SOFT;
        // an authored related ref (no marker) stays HARD.
        AgentWorkspaceAuthoring.ParseRelatedRepositories(Json($$"""[{"repositoryId":"{{RepoA}}","ref":"run-1/api","refSoftFallback":true}]"""))[0].RefSoftFallback
            .ShouldBeTrue("a session related ref is soft");
        AgentWorkspaceAuthoring.ParseRelatedRepositories(Json($$"""[{"repositoryId":"{{RepoA}}","ref":"release/1.2"}]"""))[0].RefSoftFallback
            .ShouldBeFalse("an authored related ref with no marker is hard");
    }

    [Fact]
    public void Related_session_refs_round_trip_soft_through_serialize_then_parse()
    {
        // The session base-refs map drives BOTH the ref AND its soft marker through the relatedRepositories node-input
        // JSON. A repo present in the map → soft; a repo absent from it → no ref, hard.
        var inMap = Guid.Parse(RepoA);
        var notInMap = Guid.Parse(RepoB);
        var related = new[]
        {
            new WorkspaceRepositorySpec { Alias = "api", RepositoryId = inMap, Access = WorkspaceAccess.Write },
            new WorkspaceRepositorySpec { Alias = "web", RepositoryId = notInMap, Access = WorkspaceAccess.Read },
        };

        var serialized = AgentWorkspaceAuthoring.SerializeRelatedRepositories(related, new Dictionary<Guid, string> { [inMap] = "run-1/api" });
        var reparsed = AgentWorkspaceAuthoring.ParseRelatedRepositories(Json(JsonSerializer.Serialize(serialized)));

        var apiSpec = reparsed.Single(r => r.RepositoryId == inMap);
        apiSpec.Ref.ShouldBe("run-1/api");
        apiSpec.RefSoftFallback.ShouldBeTrue("a related repo with a session base-ref is SOFT end to end (Correction-4 parity with the primary)");

        var webSpec = reparsed.Single(r => r.RepositoryId == notInMap);
        webSpec.Ref.ShouldBeNull();
        webSpec.RefSoftFallback.ShouldBeFalse("a related repo absent from the session map carries no ref ⇒ HARD, clones its default branch");
    }

    [Fact]
    public void ResolveAuthoredWorkspace_marks_the_primary_soft_when_requested()
    {
        // primaryRefSoftFallback flows onto the PRIMARY's RefSoftFallback on BOTH branches — single-repo (FromRepository)
        // and multi-repo (FromAuthoredRepos) — so a session continue's primary is soft however many repos it has.
        var single = AgentWorkspaceAuthoring.ResolveAuthoredWorkspace(Guid.Parse(RepoA), Array.Empty<WorkspaceRepositorySpec>(), "run-1/x", primaryRefSoftFallback: true);
        single!.Primary!.RefSoftFallback.ShouldBeTrue("single-repo primary is soft when requested");

        var multi = AgentWorkspaceAuthoring.ResolveAuthoredWorkspace(Guid.Parse(RepoA), new[] { Related(RepoB, "web") }, "run-1/x", primaryRefSoftFallback: true);
        multi!.Primary!.RefSoftFallback.ShouldBeTrue("multi-repo primary is soft when requested");

        var hard = AgentWorkspaceAuthoring.ResolveAuthoredWorkspace(Guid.Parse(RepoA), Array.Empty<WorkspaceRepositorySpec>(), "release/1.2");
        hard!.Primary!.RefSoftFallback.ShouldBeFalse("an authored primary ref (soft not requested) stays HARD");
    }

    [Theory]
    [InlineData("""[{"repositoryId":"11111111-1111-1111-1111-111111111111"}]""")]              // no ref key
    [InlineData("""[{"repositoryId":"11111111-1111-1111-1111-111111111111","ref":""}]""")]     // blank ref
    [InlineData("""[{"repositoryId":"11111111-1111-1111-1111-111111111111","ref":"   "}]""")]  // whitespace ref
    [InlineData("""[{"repositoryId":"11111111-1111-1111-1111-111111111111","ref":42}]""")]     // non-string ref
    public void Defaults_a_missing_or_blank_ref_to_null_so_the_repo_clones_its_default_branch(string json) =>
        AgentWorkspaceAuthoring.ParseRelatedRepositories(Json(json))[0].Ref.ShouldBeNull();

    [Theory]
    [InlineData("""[{"alias":"api"}]""")]                                  // no repositoryId
    [InlineData("""[{"repositoryId":"not-a-guid"}]""")]                    // unparseable id
    [InlineData("""[{"repositoryId":123}]""")]                             // id not a string
    [InlineData("""["a-bare-string"]""")]                                  // non-object entry
    [InlineData("""[42, true, null]""")]                                   // non-object entries
    public void Skips_a_malformed_or_idless_entry(string json) =>
        AgentWorkspaceAuthoring.ParseRelatedRepositories(Json(json)).ShouldBeEmpty("a malformed/idless entry is skipped leniently — the editor validates the authored shape");

    [Theory]
    [InlineData("[]")]                                  // empty array
    [InlineData("{}")]                                  // not an array (object)
    [InlineData("\"relatedRepositories\"")]            // not an array (string)
    [InlineData("null")]                                // not an array (null)
    public void Returns_empty_for_a_non_array_or_empty_value(string json) =>
        AgentWorkspaceAuthoring.ParseRelatedRepositories(Json(json)).ShouldBeEmpty();

    [Fact]
    public void Keeps_valid_entries_in_authored_order_dropping_only_the_invalid()
    {
        var related = AgentWorkspaceAuthoring.ParseRelatedRepositories(Json($$"""
            [{"repositoryId":"{{RepoA}}","alias":"api"}, {"alias":"orphan"}, {"repositoryId":"{{RepoB}}","alias":"web"}]
        """));

        related.Select(r => r.Alias).ShouldBe(new[] { "api", "web" }, "the idless middle entry is dropped; valid ones keep authored order");
    }

    // ── ResolveAuthoredWorkspace ─────────────────────────────────────────────────────

    private static WorkspaceRepositorySpec Related(string id, string alias) => new() { Alias = alias, RepositoryId = Guid.Parse(id), Access = WorkspaceAccess.Read };

    [Fact]
    public void ResolveAuthoredWorkspace_is_null_for_a_null_primary() =>
        AgentWorkspaceAuthoring.ResolveAuthoredWorkspace(null, new[] { Related(RepoB, "web") }).ShouldBeNull("an analysis-only run (no primary repo) has no workspace");

    [Fact]
    public void ResolveAuthoredWorkspace_is_null_when_there_are_no_related_repos() =>
        AgentWorkspaceAuthoring.ResolveAuthoredWorkspace(Guid.Parse(RepoA), Array.Empty<WorkspaceRepositorySpec>())
            .ShouldBeNull("no related repos → null Workspace → the executor derives the single-repo workspace from RepositoryId → BYTE-IDENTICAL single-repo run");

    [Fact]
    public void ResolveAuthoredWorkspace_builds_a_workspace_with_the_primary_plus_each_related()
    {
        var workspace = AgentWorkspaceAuthoring.ResolveAuthoredWorkspace(Guid.Parse(RepoA), new[] { Related(RepoB, "web") });

        workspace.ShouldNotBeNull();
        workspace!.Repositories.Count.ShouldBe(2, "the authored workspace is the primary repo plus each related repo");
        workspace.PrimaryAlias.ShouldBe(WorkspaceSpec.DefaultAlias, "the primary keeps the canonical 'repo' alias (byte-identical to a single-repo run's primary)");
        workspace.Repositories.ShouldContain(r => r.RepositoryId == Guid.Parse(RepoA) && r.IsPrimary);
        workspace.Repositories.ShouldContain(r => r.RepositoryId == Guid.Parse(RepoB));
    }

    [Fact]
    public void ResolveAuthoredWorkspace_drops_a_related_repo_that_duplicates_the_primary_no_double_clone()
    {
        // An operator double-pick (the primary listed AGAIN as a related repo) must NOT clone the repo twice into two
        // mount folders with conflicting access — the writable primary wins, the duplicate related is collapsed.
        var workspace = AgentWorkspaceAuthoring.ResolveAuthoredWorkspace(Guid.Parse(RepoA), new[] { Related(RepoA, "dup"), Related(RepoB, "web") });

        workspace.ShouldNotBeNull();
        workspace!.Repositories.Count.ShouldBe(2, "the related repo equal to the primary is dropped — a repo is cloned once");
        workspace.Repositories.Count(r => r.RepositoryId == Guid.Parse(RepoA)).ShouldBe(1, "the primary appears once (the dup related is collapsed onto it)");
        var dropped = workspace.Repositories.Single(r => r.RepositoryId == Guid.Parse(RepoA));
        dropped.IsPrimary.ShouldBeTrue("the writable primary survives, not the read-only duplicate");
        dropped.Access.ShouldBe(WorkspaceAccess.Write);
    }

    [Fact]
    public void ResolveAuthoredWorkspace_drops_a_duplicate_related_repo_keeping_the_first()
    {
        var workspace = AgentWorkspaceAuthoring.ResolveAuthoredWorkspace(Guid.Parse(RepoA), new[] { Related(RepoB, "web"), Related(RepoB, "web-again") });

        workspace!.Repositories.Count(r => r.RepositoryId == Guid.Parse(RepoB)).ShouldBe(1, "a duplicate related id is dropped — the first authored occurrence wins");
        workspace.Repositories.Single(r => r.RepositoryId == Guid.Parse(RepoB)).Alias.ShouldBe("web", "the first occurrence's alias is kept");
    }

    [Fact]
    public void ResolveAuthoredWorkspace_threads_the_primary_ref()
    {
        var workspace = AgentWorkspaceAuthoring.ResolveAuthoredWorkspace(Guid.Parse(RepoA), new[] { Related(RepoB, "web") }, primaryRef: "release/2.0");

        var primary = workspace!.Repositories.Single(r => r.IsPrimary);
        primary.Ref.ShouldBe("release/2.0", "the authored primary ref flows onto the primary repo spec");
    }

    // ── ToRelatedSpec + SerializeRelatedRepositories (the typed task-launch authoring path + its inverse) ──────────

    [Fact]
    public void ToRelatedSpec_trims_the_alias_and_defaults_access_to_read()
    {
        var spec = AgentWorkspaceAuthoring.ToRelatedSpec(Guid.Parse(RepoA), "  api  ", access: null);

        spec.RepositoryId.ShouldBe(Guid.Parse(RepoA));
        spec.Alias.ShouldBe("api", "the typed path trims the alias exactly like the JSON parse");
        spec.Access.ShouldBe(WorkspaceAccess.Read, "absent access ⇒ read-only context — the safe default");
    }

    [Theory]
    [InlineData("write", WorkspaceAccess.Write)]
    [InlineData("WRITE", WorkspaceAccess.Write)]   // case-insensitive
    [InlineData("read", WorkspaceAccess.Read)]
    [InlineData(null, WorkspaceAccess.Read)]
    [InlineData("garbage", WorkspaceAccess.Read)]
    public void ToRelatedSpec_maps_access_the_same_way_as_the_json_parse(string? access, WorkspaceAccess expected) =>
        AgentWorkspaceAuthoring.ToRelatedSpec(Guid.Parse(RepoA), null, access).Access.ShouldBe(expected);

    [Fact]
    public void ToRelatedSpec_keeps_a_blank_alias_blank_so_the_workspace_assigns_a_unique_one() =>
        AgentWorkspaceAuthoring.ToRelatedSpec(Guid.Parse(RepoA), "   ", null).Alias.ShouldBe("");

    [Fact]
    public void SerializeRelatedRepositories_is_null_for_null_or_empty()
    {
        AgentWorkspaceAuthoring.SerializeRelatedRepositories(null).ShouldBeNull();
        AgentWorkspaceAuthoring.SerializeRelatedRepositories(Array.Empty<WorkspaceRepositorySpec>()).ShouldBeNull("empty ⇒ null so the caller omits the key — byte-identical to a single-repo projection");
    }

    [Fact]
    public void SerializeRelatedRepositories_emits_the_authored_shape_omitting_a_blank_alias()
    {
        var json = AgentWorkspaceAuthoring.SerializeRelatedRepositories(new[]
        {
            new WorkspaceRepositorySpec { RepositoryId = Guid.Parse(RepoA), Alias = "api", Access = WorkspaceAccess.Write },
            new WorkspaceRepositorySpec { RepositoryId = Guid.Parse(RepoB), Alias = "", Access = WorkspaceAccess.Read },
        });

        json.ShouldNotBeNull();
        json!.Count.ShouldBe(2);
        json[0]["repositoryId"].ShouldBe(RepoA);
        json[0]["alias"].ShouldBe("api");
        json[0]["access"].ShouldBe("write");
        json[0].ContainsKey("ref").ShouldBeFalse("no base-refs map ⇒ no ref key (byte-identical to a non-continuity launch)");
        json[1]["alias"].ShouldBeNull("a blank alias is omitted so the workspace re-derives a unique one");
        json[1]["access"].ShouldBe("read");
    }

    [Fact]
    public void SerializeRelatedRepositories_emits_a_per_repo_ref_from_the_base_refs_map()
    {
        // Session branch continuity: the baseRefs map supplies each repo's prior produced branch as its clone ref.
        var baseRefs = new Dictionary<Guid, string> { [Guid.Parse(RepoA)] = "run-1/api" };   // RepoB has none

        var json = AgentWorkspaceAuthoring.SerializeRelatedRepositories(new[]
        {
            new WorkspaceRepositorySpec { RepositoryId = Guid.Parse(RepoA), Alias = "api", Access = WorkspaceAccess.Write },
            new WorkspaceRepositorySpec { RepositoryId = Guid.Parse(RepoB), Alias = "web", Access = WorkspaceAccess.Write },
        }, baseRefs)!;

        json[0]["ref"].ShouldBe("run-1/api", "the repo with a prior branch carries its ref");
        json[1].ContainsKey("ref").ShouldBeFalse("a repo ABSENT from the map carries NO ref ⇒ it clones at its default branch");
    }

    [Fact]
    public void SerializeRelatedRepositories_round_trips_through_ParseRelatedRepositories()
    {
        // The serializer is the inverse of the parse — a projection emits the EXACT shape the agent.code node + the
        // supervisor re-parse, so a launch-authored multi-repo workspace resolves identically end-to-end.
        var original = new[]
        {
            new WorkspaceRepositorySpec { RepositoryId = Guid.Parse(RepoA), Alias = "api", Access = WorkspaceAccess.Write },
            new WorkspaceRepositorySpec { RepositoryId = Guid.Parse(RepoB), Alias = "web", Access = WorkspaceAccess.Read },
        };

        var json = AgentWorkspaceAuthoring.SerializeRelatedRepositories(original)!;
        var reparsed = AgentWorkspaceAuthoring.ParseRelatedRepositories(Json(JsonSerializer.Serialize(json)));

        reparsed.Select(r => (r.RepositoryId, r.Alias, r.Access))
            .ShouldBe(original.Select(r => (r.RepositoryId, r.Alias, r.Access)));
    }

    [Fact]
    public void ResolveAuthoredWorkspace_threads_cwd_mode_into_a_multi_repo_spec_but_ignores_it_single_repo()
    {
        // The operator's multi-repo cwd choice rides through the shared authoring path onto the WorkspaceSpec. A
        // single-repo run (no related repos, no pinned ref) stays null regardless of cwdMode — the single-repo
        // invariant always runs at the repo root, so the knob is inert there (byte-identical).
        var related = new[] { new WorkspaceRepositorySpec { RepositoryId = Guid.Parse(RepoB), Alias = "web", Access = WorkspaceAccess.Read } };

        var multi = AgentWorkspaceAuthoring.ResolveAuthoredWorkspace(Guid.Parse(RepoA), related, cwdMode: WorkspaceCwdMode.PrimaryRepo);
        multi.ShouldNotBeNull();
        multi!.CwdMode.ShouldBe(WorkspaceCwdMode.PrimaryRepo, "the multi-repo spec carries the operator's cwd choice");

        AgentWorkspaceAuthoring.ResolveAuthoredWorkspace(Guid.Parse(RepoA), Array.Empty<WorkspaceRepositorySpec>(), cwdMode: WorkspaceCwdMode.PrimaryRepo)
            .ShouldBeNull("a bare single-repo run stays null even with a cwd mode set — the knob is inert single-repo");
    }

    [Fact]
    public void ResolveAuthoredWorkspace_single_repo_with_a_pinned_ref_builds_a_one_repo_spec_at_that_ref()
    {
        // Session branch continuity (S4b): a single-repo CONTINUE pins the prior turn's produced branch. A bare
        // single-repo with NO ref stays null (derives the default branch downstream — byte-identical); a pinned ref
        // needs an EXPLICIT one-repo spec so the resolver clones at it.
        var workspace = AgentWorkspaceAuthoring.ResolveAuthoredWorkspace(Guid.Parse(RepoA), Array.Empty<WorkspaceRepositorySpec>(), primaryRef: "run-1/x");

        workspace.ShouldNotBeNull("a pinned primary ref needs an explicit spec even with no related repos");
        workspace!.Repositories.Count.ShouldBe(1);
        var primary = workspace.Repositories.Single();
        primary.RepositoryId.ShouldBe(Guid.Parse(RepoA));
        primary.IsPrimary.ShouldBeTrue();
        primary.Ref.ShouldBe("run-1/x", "the pinned ref is the clone ref — the agent starts from the prior turn's branch");
    }
}
