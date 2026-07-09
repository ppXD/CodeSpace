using CodeSpace.Core.Services.Supervisor;
using Shouldly;

namespace CodeSpace.UnitTests.Supervisor;

/// <summary>DC-1 — <see cref="SupervisorOutcome.ReadPlanDelivery"/>, the reader for a plan decision's EFFECTIVE (already server-clamped) delivery contract.</summary>
[Trait("Category", "Unit")]
public class SupervisorOutcomeDeliveryTests
{
    [Fact]
    public void Reads_the_delivery_contract_off_a_plan_payload()
    {
        const string payload = """{"goal":"g","subtasks":[],"delivery":{"openPullRequest":true,"targetBranch":"main"}}""";

        var delivery = SupervisorOutcome.ReadPlanDelivery(payload);

        delivery.ShouldNotBeNull();
        delivery!.OpenPullRequest.ShouldBe(true);
        delivery.TargetBranch.ShouldBe("main");
    }

    [Fact]
    public void Reads_a_partial_contract()
    {
        const string payload = """{"goal":"g","subtasks":[],"delivery":{"openPullRequest":false}}""";

        var delivery = SupervisorOutcome.ReadPlanDelivery(payload);

        delivery!.OpenPullRequest.ShouldBe(false);
        delivery.TargetBranch.ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"goal":"g","subtasks":[]}""")]
    [InlineData("""{"goal":"g","subtasks":[],"delivery":null}""")]
    public void Reads_null_when_absent_or_malformed(string? planPayloadJson) =>
        SupervisorOutcome.ReadPlanDelivery(planPayloadJson).ShouldBeNull();
}
