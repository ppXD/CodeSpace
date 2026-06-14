using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Runtime;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the NESTED-MAP iteration-scope contract (PR-D3 decision): a flow.map branch reads its element
/// through the FIXED names {{item}} / {{index}}, so a map REPLACES the Iteration slot per branch and does
/// NOT inherit an enclosing scope's Iteration. An inner map therefore SHADOWS the outer (the inner element
/// wins; the outer element is not in scope by default) — intentional, because inheriting would collide on
/// those exact keys. Contrast BuildLoopScope, which INHERITS outer.Iteration (its own state lives under the
/// separate {{loop.*}} slot, so it can coexist with an enclosing iterate's {{item}}). A regression that made
/// the map inherit would silently change single-level behaviour the moment a map is nested.
/// </summary>
[Trait("Category", "Unit")]
public class MapBranchScopeTests
{
    private static NodeRunScope OuterWithIteration(JsonElement? outerItem)
    {
        var scope = new NodeRunScope
        {
            Trigger = new Dictionary<string, JsonElement>(),
            Iteration = outerItem is { } el
                ? new Dictionary<string, JsonElement> { ["item"] = el, ["index"] = JsonSerializer.SerializeToElement(9) }
                : null,
        };
        scope.Nodes["upstream"] = new Dictionary<string, JsonElement> { ["x"] = JsonSerializer.SerializeToElement(1) };

        return scope;
    }

    [Fact]
    public void Map_branch_seeds_a_fresh_item_and_index()
    {
        var outer = OuterWithIteration(outerItem: null);

        var branch = WorkflowEngine.BuildMapBranchScope(outer, JsonSerializer.SerializeToElement("element-A"), index: 3);

        branch.Iteration.ShouldNotBeNull();
        branch.Iteration!["item"].GetString().ShouldBe("element-A");
        branch.Iteration!["index"].GetInt32().ShouldBe(3);

        // The outer Nodes bag carries through so the body can read pre-map outputs.
        branch.Nodes.ShouldContainKey("upstream");
    }

    [Fact]
    public void Nested_map_branch_shadows_the_outer_element_not_inherits_it()
    {
        // Outer map branch is "outer-el"; the inner map then fans out "inner-el". The inner branch must see
        // ITS OWN element as {{item}} — the outer element must NOT leak through (the documented shadowing).
        var outerBranch = WorkflowEngine.BuildMapBranchScope(OuterWithIteration(outerItem: null), JsonSerializer.SerializeToElement("outer-el"), index: 0);

        var innerBranch = WorkflowEngine.BuildMapBranchScope(outerBranch, JsonSerializer.SerializeToElement("inner-el"), index: 1);

        innerBranch.Iteration!["item"].GetString().ShouldBe("inner-el", "the inner element shadows the outer one");
        innerBranch.Iteration!["index"].GetInt32().ShouldBe(1);

        // The outer element is genuinely gone from the {{item}} slot — there's no second copy hiding anywhere
        // in Iteration. (To use it inside an inner branch the author passes it down explicitly.)
        innerBranch.Iteration!.Values.ShouldNotContain(v => v.ValueKind == JsonValueKind.String && v.GetString() == "outer-el");
    }

    [Fact]
    public void Loop_scope_inherits_the_enclosing_iteration_unlike_a_map()
    {
        // The contrast that makes the map's shadowing the RIGHT call: a loop body INHERITS an enclosing
        // iterate's {{item}} (its own pass state lives under the separate {{loop.*}} slot), so a loop nested
        // under a map still sees the map's {{item}} — exactly why loop inherits and map shadows.
        var outer = OuterWithIteration(JsonSerializer.SerializeToElement("outer-el"));

        var loopScope = WorkflowEngine.BuildLoopScope(outer, outer.Nodes, new Dictionary<string, JsonElement>(), index: 0);

        loopScope.Iteration.ShouldBeSameAs(outer.Iteration, "the loop passes the enclosing iteration through by reference");
        loopScope.Iteration!["item"].GetString().ShouldBe("outer-el");
    }
}
