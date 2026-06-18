using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the pure SOTA #2 helpers on <see cref="SupervisorOutcome"/> that make the supervisor decider SEE its
/// spawned agents — <c>ProjectCompact</c> (the single shared projection the rehydrate fold AND the merge consume),
/// <c>FoldAgentResults</c> (the ADDITIVE outcome enrichment), and <c>ReadAgentResults</c>. The rehydrate WIRING
/// (terminal-scoping, DB-gate, persist-once) is proven over real Postgres in <c>SupervisorAgentResultsRehydrateFlowTests</c>;
/// this pins the decision logic in isolation. The crown jewels: the fold is ADDITIVE (agentRunIds + agentCount stay
/// byte-intact so the E5 spawn-cap / no-progress counters are unperturbed), and a Failed agent whose ResultJson is
/// null still surfaces its ROW error (the exact signal the slice exists to surface).
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorAgentResultsFoldTests
{
    private static string SpawnOutcome(params Guid[] agentRunIds) =>
        JsonSerializer.Serialize(new { agentRunIds, agentCount = agentRunIds.Length }, AgentJson.Options);

    private static string ResultJson(string? summary = null, string? error = null, string[]? changedFiles = null, string? producedBranch = null, RepositoryRunResult[]? repositoryResults = null) =>
        JsonSerializer.Serialize(new AgentRunResult
        {
            Status = Messages.Enums.AgentRunStatus.Succeeded,
            ExitReason = "completed",
            Summary = summary,
            Error = error,
            ChangedFiles = changedFiles ?? Array.Empty<string>(),
            ProducedBranch = producedBranch,
            RepositoryResults = repositoryResults ?? Array.Empty<RepositoryRunResult>(),
        }, AgentJson.Options);

    // ── ProjectCompact: the single shared compact projection ─────────────────────────

    [Fact]
    public void ProjectCompact_reads_the_compact_fields_off_the_result()
    {
        var id = Guid.NewGuid();

        var compact = SupervisorOutcome.ProjectCompact(id, "Succeeded", rowError: null,
            ResultJson(summary: "did the thing", changedFiles: new[] { "a.cs", "b.cs" }, producedBranch: "codespace/agent/x"));

        compact.AgentRunId.ShouldBe(id);
        compact.Status.ShouldBe("Succeeded");
        compact.Summary.ShouldBe("did the thing");
        compact.ChangedFiles.ShouldBe(new[] { "a.cs", "b.cs" });
        compact.ProducedBranch.ShouldBe("codespace/agent/x");
        compact.Error.ShouldBeNull();
    }

    [Fact]
    public void ProjectCompact_falls_back_to_the_ROW_error_when_the_result_is_null()
    {
        // CROWN JEWEL — a cancelled/abandoned agent sets Status=Cancelled + a ROW error but writes NO ResultJson.
        // The decider most needs to see THAT failure; reading the error only from the result would drop it.
        var compact = SupervisorOutcome.ProjectCompact(Guid.NewGuid(), "Cancelled", rowError: "lease expired", resultJson: null);

        compact.Status.ShouldBe("Cancelled");
        compact.Error.ShouldBe("lease expired", "a null-result agent still surfaces its ROW error");
        compact.ChangedFiles.ShouldBeEmpty("changedFiles is NEVER null — always an array");
        compact.Summary.ShouldBeNull();
    }

    [Fact]
    public void ProjectCompact_prefers_the_result_error_over_the_row_error()
    {
        var compact = SupervisorOutcome.ProjectCompact(Guid.NewGuid(), "Failed", rowError: "row-level", ResultJson(error: "build failed: CS1002"));

        compact.Error.ShouldBe("build failed: CS1002", "the richer result error wins when present");
    }

    [Fact]
    public void ProjectCompact_tolerates_a_corrupt_result_json()
    {
        var compact = SupervisorOutcome.ProjectCompact(Guid.NewGuid(), "Failed", rowError: "the reason", resultJson: "{not json");

        compact.Status.ShouldBe("Failed");
        compact.Error.ShouldBe("the reason", "a malformed result degrades to the row status + error, never a crash");
        compact.ChangedFiles.ShouldBeEmpty();
    }

    // ── ProjectCompact: per-repo RepositoryResults (resolver loop #379 S7-B) ──────────

    [Fact]
    public void ProjectCompact_carries_the_per_repo_RepositoryResults_off_a_multi_repo_result()
    {
        // S7-B — a multi-repo agent's per-repo outcomes ride INLINE in the compact so the resolver loop reads each
        // repo's pushed branch + identity straight off the ledger (replay-deterministic, no DB round-trip — S7-D).
        var apiId = Guid.NewGuid();
        var webId = Guid.NewGuid();

        var compact = SupervisorOutcome.ProjectCompact(Guid.NewGuid(), "Succeeded", rowError: null, ResultJson(
            summary: "coordinated change", producedBranch: "codespace/agent/api", repositoryResults: new[]
            {
                new RepositoryRunResult { Alias = "repo", RepositoryId = apiId, ChangedFiles = new[] { "Api/Foo.cs" }, ProducedBranch = "codespace/agent/api", BaseSha = "a1b2c3d4", BaseBranch = "main", Access = WorkspaceAccess.Write },
                new RepositoryRunResult { Alias = "web", RepositoryId = webId, ChangedFiles = new[] { "web/Bar.tsx" }, ProducedBranch = "codespace/agent/web", BaseBranch = "develop", Access = WorkspaceAccess.Write },
            }));

        compact.RepositoryResults.Count.ShouldBe(2);

        var api = compact.RepositoryResults.Single(r => r.Alias == "repo");
        api.RepositoryId.ShouldBe(apiId);
        api.ProducedBranch.ShouldBe("codespace/agent/api");
        api.BaseSha.ShouldBe("a1b2c3d4", "the per-repo SOTA #3 integrity anchor (the stale-base refusal SHA) is carried into the compact — S7-C/D consume it");
        api.BaseBranch.ShouldBe("main");
        api.ChangedFiles.ShouldBe(new[] { "Api/Foo.cs" });

        var web = compact.RepositoryResults.Single(r => r.Alias == "web");
        web.RepositoryId.ShouldBe(webId);
        web.ProducedBranch.ShouldBe("codespace/agent/web");
        web.BaseBranch.ShouldBe("develop");
    }

    [Fact]
    public void ProjectCompact_RepositoryResults_is_EMPTY_for_a_single_repo_result()
    {
        // The non-negotiable gate: a single-repo run has no per-repo entries — its one outcome is the top-level
        // ProducedBranch/ChangedFiles, exactly as before. EMPTY, never null, so every consumer treats it as an array.
        var compact = SupervisorOutcome.ProjectCompact(Guid.NewGuid(), "Succeeded", rowError: null,
            ResultJson(summary: "did it", changedFiles: new[] { "a.cs" }, producedBranch: "codespace/agent/x"));

        compact.RepositoryResults.ShouldBeEmpty("a single-repo result carries no per-repo entries — the 1-repo case");
        compact.ProducedBranch.ShouldBe("codespace/agent/x", "the single outcome stays on the top-level field (behaviour-identical)");
    }

    [Fact]
    public void ProjectCompact_RepositoryResults_is_empty_never_null_for_a_null_or_corrupt_result()
    {
        SupervisorOutcome.ProjectCompact(Guid.NewGuid(), "Cancelled", rowError: "gone", resultJson: null)
            .RepositoryResults.ShouldBeEmpty("a null-result agent has no per-repo outcomes — empty, never null");

        SupervisorOutcome.ProjectCompact(Guid.NewGuid(), "Failed", rowError: "x", resultJson: "{not json")
            .RepositoryResults.ShouldBeEmpty("a corrupt result degrades to empty per-repo outcomes, never a crash");
    }

    // ── FoldAgentResults: additive, byte-stable, idempotent ──────────────────────────

    [Fact]
    public void FoldAgentResults_is_ADDITIVE_keeping_agentRunIds_and_agentCount_byte_intact()
    {
        // CROWN JEWEL — the E5 counters (total-spawn cap + no-progress) read agentCount; the fold MUST NOT perturb it.
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var original = SpawnOutcome(ids);

        var folded = SupervisorOutcome.FoldAgentResults(original, new[]
        {
            new SupervisorAgentResult { AgentRunId = ids[0], Status = "Succeeded", Summary = "ok" },
            new SupervisorAgentResult { AgentRunId = ids[1], Status = "Failed", Error = "boom" },
        });

        SupervisorOutcome.ReadStagedAgentCount(folded).ShouldBe(2, "agentCount is byte-intact — the spawn-cap counter is unperturbed");
        SupervisorOutcome.ReadStagedAgentRunIds(folded).ShouldBe(ids, "agentRunIds are byte-intact in spawn order");

        var results = SupervisorOutcome.ReadAgentResults(folded);
        results.Count.ShouldBe(2);
        results[0].Status.ShouldBe("Succeeded");
        results[1].Error.ShouldBe("boom", "a failed agent's error is now visible to the decider");
    }

    [Fact]
    public void FoldAgentResults_is_idempotent_same_results_same_bytes()
    {
        var ids = new[] { Guid.NewGuid() };
        var original = SpawnOutcome(ids);
        var results = new[] { new SupervisorAgentResult { AgentRunId = ids[0], Status = "Succeeded", Summary = "ok" } };

        var once = SupervisorOutcome.FoldAgentResults(original, results);
        var twice = SupervisorOutcome.FoldAgentResults(once, results);

        twice.ShouldBe(once, "re-folding the same terminal results re-emits identical bytes → the rehydrate persist no-ops");
    }

    [Fact]
    public void FoldAgentResults_round_trips_per_repo_RepositoryResults_through_the_ledger()
    {
        // S7-B — the durable agentResults array is what the resolver loop's per-repo branch collection reads (S7-D).
        // Fold the compact in, read it back: each agent's per-repo outcomes survive the ledger JSON intact.
        var ids = new[] { Guid.NewGuid() };
        var webId = Guid.NewGuid();

        var folded = SupervisorOutcome.FoldAgentResults(SpawnOutcome(ids), new[]
        {
            new SupervisorAgentResult
            {
                AgentRunId = ids[0], Status = "Succeeded", ProducedBranch = "codespace/agent/api",
                RepositoryResults = new[]
                {
                    new RepositoryRunResult { Alias = "repo", RepositoryId = ids[0], ProducedBranch = "codespace/agent/api", BaseSha = "deadbeef", BaseBranch = "main", Access = WorkspaceAccess.Write },
                    new RepositoryRunResult { Alias = "web", RepositoryId = webId, ProducedBranch = "codespace/agent/web", BaseBranch = "develop", Access = WorkspaceAccess.Write },
                },
            },
        });

        var readBack = SupervisorOutcome.ReadAgentResults(folded).Single();

        readBack.RepositoryResults.Count.ShouldBe(2, "the per-repo outcomes survive the durable ledger round-trip");
        readBack.RepositoryResults.Select(r => r.ProducedBranch).ShouldBe(new[] { "codespace/agent/api", "codespace/agent/web" });
        readBack.RepositoryResults.Single(r => r.Alias == "web").RepositoryId.ShouldBe(webId, "per-repo identity survives — the per-repo PR-open / resolution key");
        readBack.RepositoryResults.Single(r => r.Alias == "repo").BaseSha.ShouldBe("deadbeef", "the per-repo integrity anchor (SOTA #3 stale-base refusal SHA) survives the durable ledger — S7-C/D's per-repo integrate consumes it");
    }

    [Fact]
    public void FoldAgentResults_leaves_a_zero_agent_spawn_outcome_unchanged()
    {
        // A no-op spawn records {agentRunIds:[], agentCount:0, note:"..."}; folding must NOT drop the note nor
        // trigger a spurious write (the empty-ids early-return preserves it byte-for-byte).
        var zeroAgent = JsonSerializer.Serialize(new { agentRunIds = Array.Empty<Guid>(), agentCount = 0, note = "no subtasks to spawn" }, AgentJson.Options);

        SupervisorOutcome.FoldAgentResults(zeroAgent, Array.Empty<SupervisorAgentResult>()).ShouldBe(zeroAgent);
    }

    [Fact]
    public void ReadAgentResults_is_empty_for_an_unfolded_or_malformed_outcome()
    {
        SupervisorOutcome.ReadAgentResults(SpawnOutcome(Guid.NewGuid())).ShouldBeEmpty("an unfolded spawn has no agentResults yet");
        SupervisorOutcome.ReadAgentResults(null).ShouldBeEmpty();
        SupervisorOutcome.ReadAgentResults("{not json").ShouldBeEmpty();
        SupervisorOutcome.ReadAgentResults("""{"agentResults":"oops"}""").ShouldBeEmpty("a non-array agentResults degrades to empty");
    }
}
