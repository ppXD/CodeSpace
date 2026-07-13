using CodeSpace.Core.Services.Supervisor;
using Shouldly;

namespace CodeSpace.UnitTests.Supervisor;

/// <summary>
/// 🟢 Unit: <see cref="SupervisorOutcome.ReadPlanRef"/> (P1a identity) — the plan decision's outcome is the ONE
/// immutable source of which durable plan row an attempt binds to; a malformed / legacy / rejected outcome must
/// read null (stamp nothing) rather than throw or guess.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorOutcomePlanRefTests
{
    [Fact]
    public void A_recorded_ref_reads_back_exactly()
    {
        var id = Guid.NewGuid();

        var read = SupervisorOutcome.ReadPlanRef($"{{\"planned\":[],\"count\":0,\"workPlanId\":\"{id}\",\"workPlanVersion\":3}}");

        read.ShouldNotBeNull();
        read!.Value.WorkPlanId.ShouldBe(id);
        read.Value.Version.ShouldBe(3);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("{\"planned\":[],\"count\":0}")]                                     // pre-P1a tape — no ref recorded
    [InlineData("{\"plan\":\"rejected\",\"reason\":\"no subtasks\"}")]               // rejected plan — nothing persisted
    [InlineData("{\"workPlanId\":\"not-a-guid\",\"workPlanVersion\":1}")]
    [InlineData("{\"workPlanId\":\"6f9619ff-8b86-d011-b42d-00c04fc964ff\"}")]        // version missing
    public void Anything_else_reads_null(string? outcomeJson)
    {
        SupervisorOutcome.ReadPlanRef(outcomeJson).ShouldBeNull();
    }
}
