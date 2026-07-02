using CodeSpace.Core.Services.Plans;
using CodeSpace.Messages.Plans;
using Shouldly;

namespace CodeSpace.UnitTests.Plans;

/// <summary>
/// The graph-tier plan validator (triad S2a): a structurally contradictory item DAG (duplicate ids, dangling
/// dependsOn, cycles) fails CLOSED at authoring; well-formed shapes (flat, chain, diamond) pass untouched.
/// </summary>
[Trait("Category", "Unit")]
public class WorkPlanItemGraphTests
{
    [Fact]
    public void A_flat_plan_passes()
    {
        WorkPlanItemGraph.Validate(Items(("a", null), ("b", null))).ShouldBeNull();
    }

    [Fact]
    public void A_chain_and_a_diamond_pass()
    {
        WorkPlanItemGraph.Validate(Items(("a", null), ("b", new[] { "a" }), ("c", new[] { "b" }))).ShouldBeNull("a→b→c chain is a valid DAG");

        WorkPlanItemGraph.Validate(Items(("a", null), ("b", new[] { "a" }), ("c", new[] { "a" }), ("d", new[] { "b", "c" })))
            .ShouldBeNull("the a→(b,c)→d diamond is a valid DAG");
    }

    [Fact]
    public void An_empty_plan_passes()
    {
        WorkPlanItemGraph.Validate(Array.Empty<WorkPlanItem>()).ShouldBeNull();
    }

    [Fact]
    public void A_duplicate_item_id_is_rejected()
    {
        WorkPlanItemGraph.Validate(Items(("a", null), ("a", null)))!.ShouldContain("'a' more than once");
    }

    [Fact]
    public void A_dangling_dependency_is_rejected()
    {
        WorkPlanItemGraph.Validate(Items(("a", new[] { "ghost" })))!.ShouldContain("'ghost', which the plan does not declare");
    }

    [Theory]
    [InlineData("self")]
    [InlineData("pair")]
    [InlineData("long")]
    public void A_cycle_is_rejected(string shape)
    {
        var items = shape switch
        {
            "self" => Items(("a", new[] { "a" })),
            "pair" => Items(("a", new[] { "b" }), ("b", new[] { "a" })),
            _ => Items(("a", new[] { "c" }), ("b", new[] { "a" }), ("c", new[] { "b" })),
        };

        WorkPlanItemGraph.Validate(items)!.ShouldContain("cycle");
    }

    private static IReadOnlyList<WorkPlanItem> Items(params (string Id, string[]? DependsOn)[] specs) =>
        specs.Select(s => new WorkPlanItem { Id = s.Id, Title = s.Id, Instruction = $"do {s.Id}", DependsOn = s.DependsOn }).ToList();
}
