using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the shared patch seam every "combine produced work" consumer reads through — supervisor dependency
/// staging, the supervisor merge's integrate step, and the <c>git.integrate_run</c> node. Patch offload is
/// SIZE-gated, so a producer's diff lives in EXACTLY ONE of two carriers: the artifact store (over the inline
/// threshold) or the producing agent run's own recorded result (at or below it). The publish manifest names only
/// the first, which is why a manifest-only reader saw every small diff as no diff at all and blocked the handoff.
///
/// <para>The artifact arm is proven here with NO database at all (the reader is deliberately constructed with a
/// null <c>DbContext</c>): an artifact-backed read that also reached for the run result would dereference it and
/// throw, so "never both" is pinned by construction rather than by inspection. The inline arm's database half —
/// team-scoped read of <c>agent_run.result_jsonb</c> — is proven at the integration tier against real Postgres.</para>
/// </summary>
[Trait("Category", "Unit")]
public class AgentPatchReaderTests
{
    private static readonly Guid TeamId = Guid.NewGuid();

    [Fact]
    public async Task An_offloaded_patch_resolves_from_the_artifact_store_without_ever_reading_the_run_result()
    {
        var artifactId = Guid.NewGuid();
        var reader = new AgentPatchReader(db: null!, new FakeOffloader((_, id) => id == artifactId ? "diff --git a/big b/big" : ""));

        var patch = await reader.ReadAsync(TeamId, new AgentPatchSource { AgentRunId = Guid.NewGuid(), RepositoryAlias = "primary", PatchArtifactId = artifactId }, CancellationToken.None);

        patch.ShouldBe("diff --git a/big b/big",
            "an offloaded diff resolves from the store — and the null DbContext proves it never ALSO reached for the run result, so the two carriers can't double-count");
    }

    [Fact]
    public async Task The_artifact_arm_is_byte_identical_to_the_offloader_call_staging_used_to_make_inline()
    {
        var artifactId = Guid.NewGuid();
        var seen = new List<(string? Inline, Guid? ArtifactId)>();
        var reader = new AgentPatchReader(db: null!, new FakeOffloader((inline, id) => { seen.Add((inline, id)); return "resolved"; }));

        await reader.ReadAsync(TeamId, new AgentPatchSource { AgentRunId = Guid.NewGuid(), PatchArtifactId = artifactId }, CancellationToken.None);

        seen.ShouldHaveSingleItem();
        seen[0].Inline.ShouldBeNullOrEmpty("the offloaded arm passes no competing inline text — unchanged from the pre-fix call it replaces");
        seen[0].ArtifactId.ShouldBe(artifactId);
    }

    // ── The inline carrier (pure): the rule git.integrate_run already had and staging did not ──

    [Fact]
    public void A_single_repo_result_yields_its_top_level_patch()
    {
        var resultJson = Result(new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Patch = "diff --git a/x b/x" });

        AgentInlinePatch.From(resultJson, "primary").ShouldBe("diff --git a/x b/x");
    }

    [Fact]
    public void A_multi_repo_result_yields_the_entry_matching_the_manifests_alias()
    {
        var resultJson = Result(new AgentRunResult
        {
            Status = AgentRunStatus.Succeeded,
            ExitReason = "completed",
            RepositoryResults = new[]
            {
                new RepositoryRunResult { Alias = "api", Patch = "diff-api" },
                new RepositoryRunResult { Alias = "web", Patch = "diff-web" },
            },
        });

        AgentInlinePatch.From(resultJson, "web").ShouldBe("diff-web");
        AgentInlinePatch.From(resultJson, "mobile").ShouldBe("", "an alias no entry carries resolves to nothing — never a sibling repo's diff grafted onto the wrong repository");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{not-json")]
    public void A_missing_or_unparseable_result_yields_no_bytes_never_a_guess(string? resultJson)
    {
        AgentInlinePatch.From(resultJson, "primary").ShouldBe("",
            "the integrator names such a contribution unintegrable — this layer must never fabricate patch bytes to make a handoff look clean");
    }

    private static string Result(AgentRunResult result) => JsonSerializer.Serialize(result, AgentJson.Options);

    /// <summary>Records what the reader asked the offloader for, and answers with fixed bytes — no artifact store, no database.</summary>
    private sealed class FakeOffloader : IArtifactOffloader
    {
        private readonly Func<string?, Guid?, string> _resolve;

        public FakeOffloader(Func<string?, Guid?, string> resolve) => _resolve = resolve;

        public Task<OffloadedText> OffloadIfLargeAsync(Guid teamId, string? text, string contentType, CancellationToken cancellationToken) =>
            throw new NotSupportedException("the reader never offloads — it only resolves");

        public Task<string> ResolveAsync(Guid teamId, string? inline, Guid? artifactId, CancellationToken cancellationToken) =>
            Task.FromResult(_resolve(inline, artifactId));
    }
}
