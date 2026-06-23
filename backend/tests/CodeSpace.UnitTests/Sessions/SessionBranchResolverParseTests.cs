using CodeSpace.Core.Services.Sessions;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions;

/// <summary>
/// Pins the pure heart of the session-branch resolver: <see cref="SessionBranchResolver.ReadProducedBranches"/>, which
/// decides per turn whether a turn's <c>OutputsJson</c> surfaces per-repo branches via <c>repositoryResults[]</c>
/// (multi-repo, AUTHORITATIVE) or the flat <c>branch</c> (single-repo, attributed to the sole scope repo). The
/// DB-bound scan + newest-wins de-dup is covered at the integration tier (WorkSessionBranchFlowTests); this fast unit
/// exercises every parse branch — the defensive paths (malformed JSON, non-object root, blank/idless entries) that an
/// integration seed can't easily reach.
/// </summary>
[Trait("Category", "Unit")]
public class SessionBranchResolverParseTests
{
    private static readonly Guid RepoA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RepoB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static (Guid, string)[] Read(string outputsJson, params Guid[] scope) =>
        SessionBranchResolver.ReadProducedBranches(outputsJson, scope).ToArray();

    // ── Multi-repo (repositoryResults) ───────────────────────────────────────────

    [Fact]
    public void Reads_each_repos_produced_branch_from_repositoryResults()
    {
        var json = $$"""{"repositoryResults":[{"repositoryId":"{{RepoA}}","producedBranch":"run-1/a"},{"repositoryId":"{{RepoB}}","producedBranch":"run-1/b"}]}""";

        Read(json, RepoA, RepoB).ShouldBe(new[] { (RepoA, "run-1/a"), (RepoB, "run-1/b") });
    }

    [Fact]
    public void RepositoryResults_is_authoritative_the_flat_branch_is_never_also_attributed()
    {
        // A multi-repo turn carries BOTH a flat branch (mirroring the primary) AND repositoryResults. The per-repo
        // array wins outright — the flat branch must NOT be attributed to the single scope repo (no double/ambiguous).
        var json = $$"""{"branch":"run-1/primary","repositoryResults":[{"repositoryId":"{{RepoA}}","producedBranch":"run-1/a"}]}""";

        Read(json, RepoA).ShouldBe(new[] { (RepoA, "run-1/a") }, "the per-repo array is authoritative — the flat branch is ignored when repositoryResults is present");
    }

    [Fact]
    public void Skips_a_repositoryResults_entry_with_a_blank_or_missing_produced_branch()
    {
        var json = $$"""{"repositoryResults":[{"repositoryId":"{{RepoA}}","producedBranch":""},{"repositoryId":"{{RepoB}}","producedBranch":"run-1/b"}]}""";

        Read(json, RepoA, RepoB).ShouldBe(new[] { (RepoB, "run-1/b") }, "a repo that produced no branch (blank) contributes none — it falls back to its default");
    }

    [Theory]
    [InlineData("""{"repositoryResults":[{"producedBranch":"x"}]}""")]                                       // no repositoryId
    [InlineData("""{"repositoryResults":[{"repositoryId":"not-a-guid","producedBranch":"x"}]}""")]           // unparseable id
    [InlineData("""{"repositoryResults":[{"repositoryId":123,"producedBranch":"x"}]}""")]                    // id not a string
    [InlineData("""{"repositoryResults":["a-bare-string"]}""")]                                              // non-object entry
    public void Skips_a_malformed_repositoryResults_entry(string json) =>
        Read(json, RepoA).ShouldBeEmpty("a malformed/idless per-repo entry is skipped leniently");

    [Fact]
    public void An_empty_repositoryResults_array_falls_through_to_the_flat_branch()
    {
        // A run that surfaced an empty array (no writable repo produced a branch) is NOT treated as multi-repo
        // authoritative — it falls through to the single-repo flat-branch rule.
        Read("""{"repositoryResults":[],"branch":"run-1/x"}""", RepoA).ShouldBe(new[] { (RepoA, "run-1/x") });
    }

    // ── Single-repo (flat branch) ────────────────────────────────────────────────

    [Fact]
    public void Attributes_the_flat_branch_to_the_sole_scope_repo()
    {
        Read("""{"branch":"run-1/x"}""", RepoA).ShouldBe(new[] { (RepoA, "run-1/x") });
    }

    [Fact]
    public void Does_not_attribute_a_flat_branch_when_the_scope_is_not_exactly_one_repo()
    {
        // Without repositoryResults, a flat branch on a >1-repo scope is ambiguous (it mirrors only the primary) — it
        // can't be attributed to any single repo, so it contributes nothing.
        Read("""{"branch":"run-1/x"}""", RepoA, RepoB).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("""{"branch":""}""")]      // blank
    [InlineData("""{"branch":"   "}""")]   // whitespace
    [InlineData("""{"branch":42}""")]      // non-string
    [InlineData("""{}""")]                 // absent
    public void A_blank_or_absent_flat_branch_yields_nothing(string json) =>
        Read(json, RepoA).ShouldBeEmpty();

    // ── Malformed / non-object outputs ───────────────────────────────────────────

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("[1,2,3]")]        // non-object root (array)
    [InlineData("\"a string\"")]   // non-object root (string)
    [InlineData("null")]
    public void Tolerates_malformed_or_non_object_outputs(string json) =>
        Read(json, RepoA).ShouldBeEmpty("malformed / non-object OutputsJson yields nothing — never throws");
}
