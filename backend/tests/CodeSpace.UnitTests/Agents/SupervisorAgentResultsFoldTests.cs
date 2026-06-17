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

    private static string ResultJson(string? summary = null, string? error = null, string[]? changedFiles = null, string? producedBranch = null) =>
        JsonSerializer.Serialize(new AgentRunResult
        {
            Status = Messages.Enums.AgentRunStatus.Succeeded,
            ExitReason = "completed",
            Summary = summary,
            Error = error,
            ChangedFiles = changedFiles ?? Array.Empty<string>(),
            ProducedBranch = producedBranch,
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
