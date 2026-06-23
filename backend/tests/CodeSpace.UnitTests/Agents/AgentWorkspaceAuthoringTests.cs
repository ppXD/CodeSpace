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
    public void ResolveAuthoredWorkspace_threads_the_primary_ref()
    {
        var workspace = AgentWorkspaceAuthoring.ResolveAuthoredWorkspace(Guid.Parse(RepoA), new[] { Related(RepoB, "web") }, primaryRef: "release/2.0");

        var primary = workspace!.Repositories.Single(r => r.IsPrimary);
        primary.Ref.ShouldBe("release/2.0", "the authored primary ref flows onto the primary repo spec");
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
