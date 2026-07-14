using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;
using Shouldly;
using System.Text.Json;

namespace CodeSpace.UnitTests.Completion;

/// <summary>
/// 🟢 Unit: the CurrentExecutableSet (P1b / Lock Clauses 2+3) — unit lifecycle across plan versions
/// (New/Carried/Replaced by plan-grain contract hash; cancelled = diagnostics never members), the SetHash
/// watermark, the synthetic root set for plan-less lanes, and the decision-bound supervisor computation.
/// </summary>
[Trait("Category", "Unit")]
public class ExecutableSetTests
{
    [Fact]
    public void A_replan_classifies_units_by_content_hash()
    {
        var decisions = new[]
        {
            PlanDecision(seq: 0, version: 1, out var planId, Sub("s1", "fix parser"), Sub("s2", "add tests"), Sub("s3", "old work")),
            PlanDecisionFor(planId, seq: 2, version: 2, Sub("s1", "fix parser"), Sub("s2", "add MORE tests"), Sub("s4", "new work")),
        };

        var set = SupervisorExecutableSet.Compute(decisions)!;

        set.PlanVersion.ShouldBe(2);
        set.Units.Single(u => u.UnitId == "s1").Disposition.ShouldBe(UnitDisposition.Carried);
        set.Units.Single(u => u.UnitId == "s2").Disposition.ShouldBe(UnitDisposition.Replaced);
        set.Units.Single(u => u.UnitId == "s4").Disposition.ShouldBe(UnitDisposition.New);
        set.CancelledUnitIds.ShouldBe(new[] { "s3" }, "a cancelled unit is diagnostics, never a member");
        set.Contains("s3").ShouldBeFalse();
    }

    [Fact]
    public void A_first_plan_is_all_New()
    {
        var decisions = new[] { PlanDecision(seq: 0, version: 1, out _, Sub("s1", "a"), Sub("s2", "b")) };

        SupervisorExecutableSet.Compute(decisions)!.Units.ShouldAllBe(u => u.Disposition == UnitDisposition.New);
    }

    [Fact]
    public void A_tape_without_ref_bearing_plans_has_no_set()
    {
        var legacy = new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 0, DecisionKind = SupervisorDecisionKinds.Plan, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = JsonSerializer.Serialize(new { subtasks = new[] { new { id = "s1", title = "T", instruction = "do" } } }),
            OutcomeJson = """{"planned":[],"count":1}""",
        };

        SupervisorExecutableSet.Compute(new[] { legacy }).ShouldBeNull("legacy tapes are Shadow/Legacy territory — Enforced plan-less lanes use the synthetic root instead");
    }

    [Fact]
    public void The_set_hash_is_a_stable_canonical_watermark()
    {
        var a = ExecutableSet.Create(Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff"), 1,
            new[] { Unit("s2", "hb"), Unit("s1", "ha") });
        var b = ExecutableSet.Create(Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff"), 1,
            new[] { Unit("s1", "ha"), Unit("s2", "hb") });

        a.SetHash.ShouldBe(b.SetHash, "unit order never moves the watermark — the set is a set");
        a.SetHash.ShouldStartWith("sha256/canonical-json-v1:");

        var changed = ExecutableSet.Create(Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff"), 1,
            new[] { Unit("s1", "ha"), Unit("s2", "CHANGED") });
        changed.SetHash.ShouldNotBe(a.SetHash, "any unit contract change moves the watermark (Lock Clause 2)");
    }

    [Fact]
    public void The_synthetic_root_set_is_deterministic_and_marked_version_zero()
    {
        var runId = Guid.NewGuid();

        var a = ExecutableSet.SyntheticRoot(runId, "sha256/canonical-json-v1:abc");
        var b = ExecutableSet.SyntheticRoot(runId, "sha256/canonical-json-v1:abc");

        a.SetHash.ShouldBe(b.SetHash);
        a.PlanVersion.ShouldBe(0, "version 0 marks the set synthetic — real plan versions start at 1");
        a.WorkPlanId.ShouldBe(runId);
        a.Units.ShouldHaveSingleItem().UnitId.ShouldBe(ExecutableSet.RootUnitId);
    }

    // ── Builders ──

    private static ExecutableUnit Unit(string id, string hash) => new() { UnitId = id, ContractHash = hash, Disposition = UnitDisposition.New };

    private static SupervisorPlannedSubtask Sub(string id, string instruction) => new() { Id = id, Title = "T", Instruction = instruction };

    private static SupervisorPriorDecision PlanDecision(long seq, int version, out Guid planId, params SupervisorPlannedSubtask[] subtasks)
    {
        planId = Guid.NewGuid();
        return PlanDecisionFor(planId, seq, version, subtasks);
    }

    private static SupervisorPriorDecision PlanDecisionFor(Guid planId, long seq, int version, params SupervisorPlannedSubtask[] subtasks) => new()
    {
        Id = Guid.NewGuid(), Sequence = seq, DecisionKind = SupervisorDecisionKinds.Plan, Status = SupervisorDecisionStatus.Succeeded,
        PayloadJson = JsonSerializer.Serialize(new { subtasks }, CodeSpace.Core.Services.Agents.AgentJson.Options),
        OutcomeJson = $$"""{"planned":[],"count":{{subtasks.Length}},"workPlanId":"{{planId}}","workPlanVersion":{{version}}}""",
    };
}
