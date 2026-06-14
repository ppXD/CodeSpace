using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// PR-B validator hardening — three silent-wrong-data classes the engine analysis found, closed in one
/// validator pass. Each has its own rejection case AND a paired "valid definition still passes" backstop
/// (the prime non-breaking constraint). Plus the Rule-8 drift-pin tying the validator's reserved sets to
/// the engine reducer's emitted-key constants.
///
/// <list type="number">
/// <item>BODY-REACHABLE-FROM-START — a body node disconnected from the <c>*_start</c> marker (acyclic AND
///   top-level-reachable, yet the engine runs it once per element/iteration). Generic across map / loop / try.</item>
/// <item>RESERVED-KEY + IDENTIFIER — a map <c>resultKey</c> or loop variable name that collides with a key
///   the reducer emits (silent overwrite/clobber) or isn't a usable <c>{{...}}</c> reference.</item>
/// <item>ITEMS-BINDING — a <c>flow.map</c> with no <c>items</c> binding (silent empty fan-out → green no-op).</item>
/// </list>
/// </summary>
[Trait("Category", "Unit")]
public class DefinitionValidatorContainerHardeningTests
{
    private static DefinitionValidator BuildValidator()
    {
        var nodes = new INodeRuntime[]
        {
            new StubNode("trigger.x", NodeKind.Trigger),
            new StubNode("regular.a", NodeKind.Regular),
            new StubNode("regular.b", NodeKind.Regular),
            new StubNode("flow.map", NodeKind.Map),
            new StubNode("flow.map_start", NodeKind.Regular),
            new StubNode("flow.loop", NodeKind.Loop),
            new StubNode("flow.loop_start", NodeKind.Regular),
            new StubNode("flow.try", NodeKind.Try),
            new StubNode("flow.try_start", NodeKind.Regular),
            new StubNode("builtin.terminal", NodeKind.Terminal),
        };

        return new DefinitionValidator(new NodeRegistry(nodes));
    }

    // ─── 1. Body-reachable-from-start (generic: map / loop / try) ─────────────────

    [Fact]
    public void Map_body_node_disconnected_from_start_errors()
    {
        // Body: ms → work → leaf (the connected chain) PLUS an island `orphan` with NO edge from ms.
        // orphan is acyclic and the map is top-level reachable, but the engine's frontier walk would run
        // orphan once per element (no incoming body edge ⇒ a root). Reject it.
        var def = new WorkflowDefinition
        {
            Nodes = new List<NodeDefinition>
            {
                Node("t", "trigger.x"),
                MapNode("map"),
                Node("end", "builtin.terminal"),
                Body("ms", "flow.map_start", "map"),
                Body("work", "regular.a", "map"),
                Body("leaf", "builtin.terminal", "map"),
                Body("orphan", "regular.b", "map"),   // ⇐ disconnected from ms
            },
            Edges = new List<EdgeDefinition> { Edge("t", "map"), Edge("map", "end"), Edge("ms", "work"), Edge("work", "leaf") },
        };

        var result = BuildValidator().Validate(def);
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("'orphan'") && e.Contains("not reachable from flow.map_start"));
    }

    [Fact]
    public void Loop_body_node_disconnected_from_start_errors()
    {
        // loop/try had NO body-start reachability check before PR-B. ls → work connected; orphan disconnected.
        var def = new WorkflowDefinition
        {
            Nodes = new List<NodeDefinition>
            {
                Node("t", "trigger.x"),
                Node("loop", "flow.loop"),
                Node("end", "builtin.terminal"),
                Body("ls", "flow.loop_start", "loop"),
                Body("work", "regular.a", "loop"),
                Body("orphan", "regular.b", "loop"),   // ⇐ disconnected from ls
            },
            Edges = new List<EdgeDefinition> { Edge("t", "loop"), Edge("loop", "end"), Edge("ls", "work") },
        };

        var result = BuildValidator().Validate(def);
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("'orphan'") && e.Contains("not reachable from flow.loop_start"));
    }

    [Fact]
    public void Try_body_node_disconnected_from_start_errors()
    {
        var def = new WorkflowDefinition
        {
            Nodes = new List<NodeDefinition>
            {
                Node("t", "trigger.x"),
                Node("try", "flow.try"),
                Node("end", "builtin.terminal"),
                Body("ts", "flow.try_start", "try"),
                Body("work", "regular.a", "try"),
                Body("orphan", "regular.b", "try"),   // ⇐ disconnected from ts
            },
            Edges = new List<EdgeDefinition> { Edge("t", "try"), Edge("try", "end"), Edge("ts", "work") },
        };

        var result = BuildValidator().Validate(def);
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("'orphan'") && e.Contains("not reachable from flow.try_start"));
    }

    [Fact]
    public void Fully_connected_loop_body_passes()
    {
        // The non-breaking backstop for loop: ls → work, every body node reachable — must still validate.
        var def = new WorkflowDefinition
        {
            Nodes = new List<NodeDefinition>
            {
                Node("t", "trigger.x"),
                Node("loop", "flow.loop"),
                Node("end", "builtin.terminal"),
                Body("ls", "flow.loop_start", "loop"),
                Body("work", "regular.a", "loop"),
            },
            Edges = new List<EdgeDefinition> { Edge("t", "loop"), Edge("loop", "end"), Edge("ls", "work") },
        };

        BuildValidator().Validate(def).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Fully_connected_try_body_passes()
    {
        var def = new WorkflowDefinition
        {
            Nodes = new List<NodeDefinition>
            {
                Node("t", "trigger.x"),
                Node("try", "flow.try"),
                Node("end", "builtin.terminal"),
                Body("ts", "flow.try_start", "try"),
                Body("work", "regular.a", "try"),
            },
            Edges = new List<EdgeDefinition> { Edge("t", "try"), Edge("try", "end"), Edge("ts", "work") },
        };

        BuildValidator().Validate(def).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Body_node_reachable_via_in_body_error_edge_passes()
    {
        // Engine-fidelity: an in-body `error` edge is a normal body edge the engine traverses. ms → boom, and
        // boom =(error)=> handler. handler is reachable across the error edge → no false rejection.
        var def = new WorkflowDefinition
        {
            Nodes = new List<NodeDefinition>
            {
                Node("t", "trigger.x"),
                MapNode("map"),
                Node("end", "builtin.terminal"),
                Body("ms", "flow.map_start", "map"),
                Body("boom", "regular.a", "map"),
                Body("handler", "builtin.terminal", "map"),
            },
            Edges = new List<EdgeDefinition>
            {
                Edge("t", "map"), Edge("map", "end"), Edge("ms", "boom"),
                new() { From = "boom", To = "handler", SourceHandle = WorkflowHandles.Error },
            },
        };

        BuildValidator().Validate(def).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("flow.loop", "flow.loop_start", 0)]   // loop body with NO start — every source-only body node would fan out per iteration
    [InlineData("flow.loop", "flow.loop_start", 2)]   // loop body with TWO starts — a 2nd component runs once per iteration
    [InlineData("flow.try", "flow.try_start", 0)]     // try body with NO start
    [InlineData("flow.try", "flow.try_start", 2)]     // try body with TWO starts
    public void Loop_or_try_body_without_exactly_one_start_errors(string containerType, string startType, int startCount)
    {
        // loop/try had NO body-start shape check before this fix — a 0/2-start body passed everything yet the
        // engine seeds every zero-incoming body node as a root and runs it once per iteration. Now rejected.
        var body = new List<NodeDefinition> { Body("work", "regular.a", "c"), Body("leaf", "builtin.terminal", "c") };
        for (var i = 0; i < startCount; i++) body.Add(Body($"s{i}", startType, "c"));

        var nodes = new List<NodeDefinition> { Node("t", "trigger.x"), Node("c", containerType), Node("end", "builtin.terminal") };
        nodes.AddRange(body);

        var edges = new List<EdgeDefinition> { Edge("t", "c"), Edge("c", "end"), Edge("work", "leaf") };
        for (var i = 0; i < startCount; i++) edges.Add(Edge($"s{i}", "work"));

        var result = BuildValidator().Validate(new WorkflowDefinition { Nodes = nodes, Edges = edges });
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains($"exactly one {startType}") && e.Contains($"found {startCount}"));
    }

    [Fact]
    public void Nested_container_node_reachable_in_outer_body_passes()
    {
        // Engine-fidelity: the outer body walk reaches the nested loop NODE and stops — the loop's own children
        // (ParentId == loop) are NOT outer-body nodes. ms → loop; the loop body (ls → gate) is its own subgraph.
        var def = new WorkflowDefinition
        {
            Nodes = new List<NodeDefinition>
            {
                Node("t", "trigger.x"),
                MapNode("map"),
                Node("end", "builtin.terminal"),
                Body("ms", "flow.map_start", "map"),
                Body("loop", "flow.loop", "map"),
                Body("ls", "flow.loop_start", "loop"),
                Body("inner", "regular.a", "loop"),
            },
            Edges = new List<EdgeDefinition> { Edge("t", "map"), Edge("map", "end"), Edge("ms", "loop"), Edge("ls", "inner") },
        };

        BuildValidator().Validate(def).IsValid.ShouldBeTrue();
    }

    // ─── 2. Reserved-key + identifier (map resultKey / loop var names) ────────────

    [Theory]
    [InlineData("count")]
    [InlineData("failed")]
    public void Map_resultKey_equal_to_a_reserved_key_errors(string reserved)
    {
        var result = BuildValidator().Validate(MapWith(resultKey: reserved));
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains($"resultKey '{reserved}'") && e.Contains("reserved"));
    }

    [Theory]
    [InlineData("my key")]   // space
    [InlineData("1x")]       // leading digit
    [InlineData("a-b")]      // hyphen
    [InlineData("a.b")]      // dot — would split the reference path
    public void Map_resultKey_that_is_not_an_identifier_errors(string badKey)
    {
        var result = BuildValidator().Validate(MapWith(resultKey: badKey));
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("resultKey") && e.Contains("not a valid output key"));
    }

    [Theory]
    [InlineData("results")]   // the engine default — explicitly valid
    [InlineData("answers")]
    [InlineData("_private")]
    [InlineData("subtask_results")]
    public void Map_resultKey_that_is_a_valid_identifier_passes(string goodKey)
    {
        BuildValidator().Validate(MapWith(resultKey: goodKey)).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Map_with_no_resultKey_uses_the_default_and_passes()
    {
        // No resultKey in config ⇒ engine defaults to "results"; must not be rejected.
        BuildValidator().Validate(MapWith(resultKey: null)).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("ResultKey")]   // PascalCase
    [InlineData("RESULTKEY")]   // upper
    public void Map_resultKey_with_non_canonical_casing_is_still_checked(string propName)
    {
        // The engine parses MapConfig case-insensitively, so "ResultKey":"count" still collides at
        // run time. The validator must catch the reserved key regardless of property-name casing.
        var result = BuildValidator().Validate(MapWith(rawConfig: $$"""{ "{{propName}}": "count" }"""));
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("resultKey 'count'") && e.Contains("reserved"));
    }

    [Theory]
    [InlineData("iterations")]
    [InlineData("failedIterations")]
    [InlineData("terminationReason")]
    [InlineData("index")]   // the injected iteration-scope index
    public void Loop_variable_equal_to_a_reserved_key_errors(string reserved)
    {
        var result = BuildValidator().Validate(LoopWithVar(reserved));
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains($"variable '{reserved}'") && e.Contains("reserved"));
    }

    [Theory]
    [InlineData("my var")]
    [InlineData("2nd")]
    [InlineData("a-b")]
    public void Loop_variable_that_is_not_an_identifier_errors(string badName)
    {
        var result = BuildValidator().Validate(LoopWithVar(badName));
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("variable") && e.Contains("not a valid name"));
    }

    [Theory]
    [InlineData("acc")]
    [InlineData("total")]
    [InlineData("_carry")]
    public void Loop_variable_with_a_valid_name_passes(string goodName)
    {
        BuildValidator().Validate(LoopWithVar(goodName)).IsValid.ShouldBeTrue();
    }

    // ─── 3. Items binding required ────────────────────────────────────────────────

    [Fact]
    public void Map_with_no_items_binding_errors()
    {
        var result = BuildValidator().Validate(MapWith(itemsJson: null));
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("no 'items' binding"));
    }

    [Theory]
    [InlineData("""{ "items": "" }""")]        // blank string
    [InlineData("""{ "items": null }""")]      // explicit null
    [InlineData("""{ "items": [] }""")]        // empty inline array
    [InlineData("""{ "other": "x" }""")]       // items key absent
    public void Map_with_an_empty_or_absent_items_binding_errors(string inputsJson)
    {
        var result = BuildValidator().Validate(MapWith(itemsJson: inputsJson));
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("no 'items' binding"));
    }

    [Theory]
    [InlineData("""{ "items": "{{trigger.things}}" }""")]                       // {{...}} template ref (opaque trigger scope)
    [InlineData("""{ "items": "{{wf.things}}" }""")]                            // {{...}} workflow-variable ref
    [InlineData("""{ "items": ["a", "b"] }""")]                                 // inline literal array
    [InlineData("""{ "items": { "$ref": "trigger.things" } }""")]               // $ref object form (opaque trigger scope)
    public void Map_with_a_present_non_empty_items_binding_passes(string inputsJson)
    {
        var result = BuildValidator().Validate(MapWith(itemsJson: inputsJson));
        result.IsValid.ShouldBeTrue(string.Join(" || ", result.Errors));
    }

    // ─── 4. Rule-8 drift pin: validator reserved sets == engine reducer key constants ──

    [Fact]
    public void Map_reserved_set_is_pinned_to_the_engine_reducer_keys()
    {
        // BuildMapOutputs writes the result array under resultKey, then count + failed. If a future key is
        // added to the reducer but not here, this fails — forcing the validator's reserved set to follow.
        WorkflowOutputKeys.Map.ShouldBe(new[] { "count", "failed" });
    }

    [Fact]
    public void Loop_reserved_set_is_pinned_to_the_engine_reducer_keys_plus_index()
    {
        // BuildLoopOutputs writes iterations/failedIterations/terminationReason after the loop-var spread;
        // BuildLoopScope injects index into the per-pass loop scope. All four clobber a same-named loop var.
        WorkflowOutputKeys.Loop.ShouldBe(new[] { "iterations", "failedIterations", "terminationReason", "index" });
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────────

    // manual → map(items; body: ms → work → leaf) → terminal. The canonical valid map the hardening checks
    // must leave alone; each scenario tweaks one knob (resultKey / items) off this baseline.
    private static WorkflowDefinition MapWith(string? resultKey = "results", string? itemsJson = """{ "items": "{{trigger.things}}" }""", string? rawConfig = null)
    {
        var config = rawConfig ?? (resultKey == null ? "{}" : $$"""{ "resultKey": "{{resultKey}}" }""");

        var map = new NodeDefinition
        {
            Id = "map", TypeKey = "flow.map",
            Config = Parse(config),
            Inputs = itemsJson == null ? Empty() : Parse(itemsJson),
        };

        return new WorkflowDefinition
        {
            Nodes = new List<NodeDefinition>
            {
                Node("t", "trigger.x"),
                map,
                Node("end", "builtin.terminal"),
                Body("ms", "flow.map_start", "map"),
                Body("work", "regular.a", "map"),
                Body("leaf", "builtin.terminal", "map"),
            },
            Edges = new List<EdgeDefinition> { Edge("t", "map"), Edge("map", "end"), Edge("ms", "work"), Edge("work", "leaf") },
        };
    }

    // manual → loop(one variable; body: ls → work) → terminal.
    private static WorkflowDefinition LoopWithVar(string varName)
    {
        var loop = new NodeDefinition
        {
            Id = "loop", TypeKey = "flow.loop",
            Config = Parse($$"""{ "loopVariables": [ { "name": "{{varName}}", "value": 0 } ], "maxIterations": 2 }"""),
            Inputs = Empty(),
        };

        return new WorkflowDefinition
        {
            Nodes = new List<NodeDefinition>
            {
                Node("t", "trigger.x"),
                loop,
                Node("end", "builtin.terminal"),
                Body("ls", "flow.loop_start", "loop"),
                Body("work", "regular.a", "loop"),
            },
            Edges = new List<EdgeDefinition> { Edge("t", "loop"), Edge("loop", "end"), Edge("ls", "work") },
        };
    }

    private static NodeDefinition MapNode(string id) => new() { Id = id, TypeKey = "flow.map", Config = Empty(), Inputs = Parse("""{ "items": "{{trigger.things}}" }""") };

    private static JsonElement Empty() => JsonDocument.Parse("{}").RootElement.Clone();
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static EdgeDefinition Edge(string from, string to) => new() { From = from, To = to };
    private static NodeDefinition Node(string id, string typeKey) => new() { Id = id, TypeKey = typeKey, Config = Empty(), Inputs = Empty() };
    private static NodeDefinition Body(string id, string typeKey, string parentId) => new() { Id = id, TypeKey = typeKey, ParentId = parentId, Config = Empty(), Inputs = Empty() };

    private sealed class StubNode : INodeRuntime
    {
        public StubNode(string typeKey, NodeKind kind)
        {
            TypeKey = typeKey;
            Manifest = new NodeManifest
            {
                DisplayName = typeKey,
                Category = "Test",
                Kind = kind,
                ConfigSchema = SchemaBuilder.EmptyObject(),
                InputSchema = SchemaBuilder.EmptyObject(),
                OutputSchema = SchemaBuilder.EmptyObject(),
            };
        }

        public string TypeKey { get; }
        public NodeManifest Manifest { get; }
        public Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken) => Task.FromResult(NodeResult.Ok());
    }
}
