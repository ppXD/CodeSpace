using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the PURE pieces of loopability slice 3 (per-unit objective acceptance). The rehydrate WIRING (grade-once,
/// positional join, fail-closed, repo resolution) is proven over real Postgres in
/// <c>SupervisorUnitAcceptanceFoldFlowTests</c>; this pins the decision logic in isolation: (1) the no-progress
/// EVIDENCE DISCOUNT — an objectively-rejected unit (<c>AcceptancePassed == false</c>) is NOT settled evidence even
/// though it pushed a branch (the must-fix: without it an acceptance-failing retry loop never trips the stall bound);
/// (2) <c>ReadPlanSubtasks</c> reads each subtask's authored acceptance off a plan payload; (3) the new verdict fields
/// are null-omitted so an ungraded unit serializes byte-identical.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorUnitAcceptanceTests
{
    private static SupervisorAgentResult BranchPushed(bool? acceptancePassed) => new()
    {
        AgentRunId = Guid.NewGuid(), Status = "Succeeded", ProducedBranch = "codespace/agent/x", AcceptancePassed = acceptancePassed,
    };

    // ── The no-progress evidence discount (the must-fix) ───────────────────────────────

    [Theory]
    [InlineData(null, true)]   // ungraded (no per-unit contract) — branch counts, byte-identical to pre-slice
    [InlineData(true, true)]   // graded PASS — verified work is evidence
    [InlineData(false, false)] // graded FAIL — a branch pushed but REJECTED is NOT progress (the discount)
    public void A_rejected_unit_is_discounted_from_settled_evidence_even_with_a_branch(bool? acceptancePassed, bool expectedEvidence)
    {
        SupervisorOutcome.HasSettledEvidence(new[] { BranchPushed(acceptancePassed) })
            .ShouldBe(expectedEvidence, "an objectively-rejected unit must not reset the no-progress streak; ungraded/passed are unchanged");
    }

    [Fact]
    public void A_self_reported_Succeeded_unit_that_failed_acceptance_is_not_evidence()
    {
        // Objective truth overrides the self-report: a unit can claim Succeeded yet fail its own definition-of-done.
        var result = new SupervisorAgentResult { AgentRunId = Guid.NewGuid(), Status = "Succeeded", ChangedFiles = new[] { "a.cs" }, AcceptancePassed = false };

        SupervisorOutcome.HasSettledEvidence(new[] { result }).ShouldBeFalse("a rejected unit is not progress even when its row says Succeeded with changed files");
    }

    [Fact]
    public void A_wave_with_one_accepted_unit_among_rejected_ones_still_has_evidence()
    {
        var results = new[] { BranchPushed(false), BranchPushed(false), BranchPushed(true) };

        SupervisorOutcome.HasSettledEvidence(results).ShouldBeTrue("at least one objectively-accepted unit is real forward progress");
    }

    // ── ReadPlanSubtasks: the per-unit acceptance source ───────────────────────────────

    [Fact]
    public void ReadPlanSubtasks_reads_each_subtask_and_its_authored_acceptance()
    {
        const string planPayload = """
            {"goal":"g","subtasks":[
              {"id":"s1","title":"scaffold","instruction":"do"},
              {"id":"s2","title":"wire","instruction":"do","dependsOn":["s1"],"acceptance":{"command":["make","test"],"description":"green"}}
            ]}
            """;

        var subtasks = SupervisorOutcome.ReadPlanSubtasks(planPayload);

        subtasks.Count.ShouldBe(2);
        subtasks[0].Acceptance.ShouldBeNull("a subtask without a contract carries none");
        subtasks[1].DependsOn.ShouldBe(new[] { "s1" });
        subtasks[1].Acceptance!.Command.ShouldBe(new[] { "make", "test" });
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    public void ReadPlanSubtasks_is_empty_for_absent_or_malformed_payloads(string payload)
    {
        SupervisorOutcome.ReadPlanSubtasks(payload).ShouldBeEmpty();
    }

    // ── Byte-identity: the verdict fields are invisible when ungraded ──────────────────

    [Fact]
    public void An_ungraded_result_omits_both_verdict_fields()
    {
        var json = JsonSerializer.Serialize(new SupervisorAgentResult { AgentRunId = Guid.NewGuid(), Status = "Succeeded" }, AgentJson.Options);

        json.ShouldNotContain("acceptancePassed", Case.Insensitive, "a null verdict must be omitted — the durable agentResults bytes stay identical to pre-slice");
        json.ShouldNotContain("acceptanceDetail", Case.Insensitive);
    }

    [Fact]
    public void A_graded_result_round_trips_the_verdict()
    {
        var original = new SupervisorAgentResult { AgentRunId = Guid.NewGuid(), Status = "Succeeded", ProducedBranch = "b", AcceptancePassed = false, AcceptanceDetail = "tests-failed-exit-1" };

        var back = JsonSerializer.Deserialize<SupervisorAgentResult>(JsonSerializer.Serialize(original, AgentJson.Options), AgentJson.Options)!;

        back.AcceptancePassed.ShouldBe(false);
        back.AcceptanceDetail.ShouldBe("tests-failed-exit-1");
    }

    // ── The positional invariant the per-unit join depends on: crash-recovery re-park must re-derive agentRunIds in
    //    NUMERIC spawn-index order. The wait key's #{k} is non-zero-padded, so a lexicographic sort scrambles K≥11. ──

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(15)]
    public void SpawnIndexOf_parses_the_trailing_index_off_the_wait_key(int k)
    {
        SupervisorOutcome.SpawnIndexOf(SupervisorOutcome.AgentWaitKey("sup", turnNumber: 1, spawnIndex: k)).ShouldBe(k);
    }

    [Fact]
    public void SpawnIndexOf_sorts_a_malformed_key_last_rather_than_throwing()
    {
        SupervisorOutcome.SpawnIndexOf("sup#turn1#notanumber").ShouldBe(int.MaxValue);
        SupervisorOutcome.SpawnIndexOf("nohash").ShouldBe(int.MaxValue);
    }

    [Fact]
    public void Ordering_re_park_keys_by_spawn_index_preserves_numeric_order_for_K_over_ten()
    {
        // The 12 per-turn wait keys a K=12 spawn staged, in numeric order. A lexicographic sort would yield
        // #0,#1,#10,#11,#2,… — scrambling agentRunIds out of subtaskIds[i] order and corrupting the per-unit grade.
        var numeric = Enumerable.Range(0, 12).Select(k => SupervisorOutcome.AgentWaitKey("sup", 1, k)).ToList();
        var scrambled = numeric.OrderBy(key => key, StringComparer.Ordinal).ToList();

        scrambled.ShouldNotBe(numeric, "precondition: a lexicographic order genuinely differs at K≥11 (the test has teeth)");
        scrambled.OrderBy(SupervisorOutcome.SpawnIndexOf).ToList()
            .ShouldBe(numeric, "ordering the re-derived waits by parsed spawn index restores the authored subtaskIds[i] order");
    }
}
