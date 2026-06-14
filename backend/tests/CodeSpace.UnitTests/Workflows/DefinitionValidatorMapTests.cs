using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the container-structure rules <see cref="DefinitionValidator"/> adds for <c>flow.map</c> (which
/// <c>flow.loop</c> never had): a non-empty body rooted at exactly one <c>flow.map_start</c> and ending
/// in exactly one terminal (the per-element result source), no edge crossing the container boundary (the
/// engine silently drops those today), and a save-time nesting-depth guard. Each rejection has its own
/// case so a relaxed check can't slip through. A well-formed map passes — the regression backstop.
///
/// <para>PR2: a SUSPENDING body node (manifest CanSuspend) now VALIDATES — the PR1 fail-closed guard that
/// rejected it at save time is gone, because durable parallel-branch resume now parks each branch under
/// its own iteration key and resumes from the ledger. <see cref="Map_body_with_a_suspending_node_validates"/>
/// is the acceptance test that the guard was truly lifted.</para>
/// </summary>
[Trait("Category", "Unit")]
public class DefinitionValidatorMapTests
{
    private static DefinitionValidator BuildValidator()
    {
        var nodes = new INodeRuntime[]
        {
            new MapStubNode("trigger.x", NodeKind.Trigger),
            new MapStubNode("regular.a", NodeKind.Regular),
            new MapStubNode("regular.b", NodeKind.Regular),
            new MapStubNode("flow.map", NodeKind.Map),
            new MapStubNode("flow.map_start", NodeKind.Regular),
            new MapStubNode("flow.loop", NodeKind.Loop),
            new MapStubNode("flow.loop_start", NodeKind.Regular),
            new MapStubNode("flow.wait_approval", NodeKind.Regular, canSuspend: true),
            new MapStubNode("builtin.terminal", NodeKind.Terminal),
        };

        return new DefinitionValidator(new NodeRegistry(nodes));
    }

    [Fact]
    public void Well_formed_map_passes()
    {
        BuildValidator().Validate(WellFormedMap()).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Empty_map_body_errors()
    {
        // A map with no body nodes at all.
        var def = new WorkflowDefinition
        {
            Nodes = new List<NodeDefinition>
            {
                Node("t", "trigger.x"),
                Node("map", "flow.map"),
                Node("end", "builtin.terminal"),
            },
            Edges = new List<EdgeDefinition> { Edge("t", "map"), Edge("map", "end") },
        };

        var result = BuildValidator().Validate(def);
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("empty body"));
    }

    [Theory]
    [InlineData(0)]   // no map_start at all
    [InlineData(2)]   // two map_starts
    public void Map_body_without_exactly_one_start_errors(int startCount)
    {
        var body = new List<NodeDefinition> { Body("work", "regular.a", "map"), Body("leaf", "builtin.terminal", "map") };
        for (var i = 0; i < startCount; i++) body.Add(Body($"ms{i}", "flow.map_start", "map"));

        var nodes = new List<NodeDefinition> { Node("t", "trigger.x"), Node("map", "flow.map"), Node("end", "builtin.terminal") };
        nodes.AddRange(body);

        var edges = new List<EdgeDefinition> { Edge("t", "map"), Edge("map", "end"), Edge("work", "leaf") };
        for (var i = 0; i < startCount; i++) edges.Add(Edge($"ms{i}", "work"));

        var result = BuildValidator().Validate(new WorkflowDefinition { Nodes = nodes, Edges = edges });
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("exactly one flow.map_start"));
    }

    [Fact]
    public void Map_body_with_two_terminals_errors()
    {
        // map_start fans to two leaves, neither with an outgoing edge ⇒ two terminals ⇒ ambiguous result.
        var def = new WorkflowDefinition
        {
            Nodes = new List<NodeDefinition>
            {
                Node("t", "trigger.x"), Node("map", "flow.map"), Node("end", "builtin.terminal"),
                Body("ms", "flow.map_start", "map"),
                Body("leafA", "regular.a", "map"),
                Body("leafB", "regular.b", "map"),
            },
            Edges = new List<EdgeDefinition> { Edge("t", "map"), Edge("map", "end"), Edge("ms", "leafA"), Edge("ms", "leafB") },
        };

        var result = BuildValidator().Validate(def);
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("exactly one terminal"));
    }

    [Fact]
    public void Map_body_of_only_the_start_marker_errors()
    {
        // A body that is JUST flow.map_start: the start has no outgoing edge, so it is itself the single
        // terminal ⇒ results[i] would be the passthrough start's empty output (a no-op map). Reject it.
        var def = new WorkflowDefinition
        {
            Nodes = new List<NodeDefinition>
            {
                Node("t", "trigger.x"), Node("map", "flow.map"), Node("end", "builtin.terminal"),
                Body("ms", "flow.map_start", "map"),
            },
            Edges = new List<EdgeDefinition> { Edge("t", "map"), Edge("map", "end") },
        };

        var result = BuildValidator().Validate(def);
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("at least one node after flow.map_start"));
    }

    [Fact]
    public void Map_body_with_a_suspending_node_validates()
    {
        // PR2 acceptance: a body node that can SUSPEND (here flow.wait_approval, manifest CanSuspend=true)
        // is now ALLOWED — the PR1 fail-closed guard is gone. Each branch parks under its own iteration key
        // and the run resumes from the ledger when every branch wait resolves. This is the headline of the
        // planner+parallel-subagents epic: a map body whose element is an agent.code that parks to an AgentRun.
        var def = new WorkflowDefinition
        {
            Nodes = new List<NodeDefinition>
            {
                Node("t", "trigger.x"),
                NodeWithInputs("map", "flow.map", """{ "items": "{{trigger.things}}" }"""),
                Node("end", "builtin.terminal"),
                Body("ms", "flow.map_start", "map"),
                Body("gate", "flow.wait_approval", "map"),   // ⇐ parks the run — a first-class PR2 map body element
            },
            Edges = new List<EdgeDefinition> { Edge("t", "map"), Edge("map", "end"), Edge("ms", "gate") },
        };

        BuildValidator().Validate(def).IsValid.ShouldBeTrue("PR2 lifts the PR1 no-suspend-in-map-body guard");
    }

    [Fact]
    public void Map_body_with_a_suspending_node_in_a_NESTED_container_validates()
    {
        // PR2: the lifted guard also applies to a suspend buried in a nested container — an approval inside a
        // flow.loop whose ParentId is the map validates just like a direct body suspend. (The other container
        // rules — single terminal, no boundary-crossing edges, depth — still apply to the nested loop.)
        var def = new WorkflowDefinition
        {
            Nodes = new List<NodeDefinition>
            {
                Node("t", "trigger.x"),
                NodeWithInputs("map", "flow.map", """{ "items": "{{trigger.things}}" }"""),
                Node("end", "builtin.terminal"),
                Body("ms", "flow.map_start", "map"),
                Body("loop", "flow.loop", "map"),                  // nested container in the map body
                Body("ls", "flow.loop_start", "loop"),
                Body("gate", "flow.wait_approval", "loop"),        // ⇐ buried two levels deep — now allowed
            },
            Edges = new List<EdgeDefinition> { Edge("t", "map"), Edge("map", "end"), Edge("ms", "loop"), Edge("ls", "gate") },
        };

        BuildValidator().Validate(def).IsValid.ShouldBeTrue("a suspend nested under a map body container validates in PR2");
    }

    [Fact]
    public void Edge_crossing_the_container_boundary_errors()
    {
        // A body node wired directly to a top-level node — the engine's SubgraphView would silently drop it.
        var def = new WorkflowDefinition
        {
            Nodes = new List<NodeDefinition>
            {
                Node("t", "trigger.x"), Node("map", "flow.map"), Node("end", "builtin.terminal"),
                Body("ms", "flow.map_start", "map"),
                Body("work", "regular.a", "map"),
            },
            Edges = new List<EdgeDefinition>
            {
                Edge("t", "map"), Edge("map", "end"), Edge("ms", "work"),
                Edge("work", "end"),   // ⇐ crosses out of the body to a top-level node
            },
        };

        var result = BuildValidator().Validate(def);
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("crosses a container boundary"));
    }

    [Fact]
    public void Excessive_container_nesting_errors()
    {
        // A ParentId chain 9 containers deep (cap is 8). Build map_0 (top) → map_1 → … → map_9, each a
        // body child of the previous; the deepest exceeds the limit.
        var nodes = new List<NodeDefinition> { Node("t", "trigger.x"), Node("end", "builtin.terminal") };
        var edges = new List<EdgeDefinition> { Edge("t", "map_0"), Edge("map_0", "end") };

        for (var i = 0; i <= 9; i++)
        {
            var id = $"map_{i}";
            var parent = i == 0 ? null : $"map_{i - 1}";
            nodes.Add(new NodeDefinition { Id = id, TypeKey = "flow.map", ParentId = parent, Config = Empty(), Inputs = Empty() });
        }

        var result = BuildValidator().Validate(new WorkflowDefinition { Nodes = nodes, Edges = edges });
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("nested deeper than"));
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    // manual → map(body: ms → work → leaf) → terminal. work reads {{item}}; leaf is the result source.
    private static WorkflowDefinition WellFormedMap() => new()
    {
        Nodes = new List<NodeDefinition>
        {
            Node("t", "trigger.x"),
            NodeWithInputs("map", "flow.map", """{ "items": "{{trigger.things}}" }"""),
            Node("end", "builtin.terminal"),
            Body("ms", "flow.map_start", "map"),
            BodyWithInputs("work", "regular.a", "map", """{ "v": "{{item}}", "i": "{{index}}" }"""),
            Body("leaf", "builtin.terminal", "map"),
        },
        Edges = new List<EdgeDefinition> { Edge("t", "map"), Edge("map", "end"), Edge("ms", "work"), Edge("work", "leaf") },
    };

    private static JsonElement Empty() => JsonDocument.Parse("{}").RootElement.Clone();
    private static EdgeDefinition Edge(string from, string to) => new() { From = from, To = to };

    private static NodeDefinition Node(string id, string typeKey) => new() { Id = id, TypeKey = typeKey, Config = Empty(), Inputs = Empty() };
    private static NodeDefinition NodeWithInputs(string id, string typeKey, string inputsJson) => new() { Id = id, TypeKey = typeKey, Config = Empty(), Inputs = JsonDocument.Parse(inputsJson).RootElement.Clone() };
    private static NodeDefinition Body(string id, string typeKey, string parentId) => new() { Id = id, TypeKey = typeKey, ParentId = parentId, Config = Empty(), Inputs = Empty() };
    private static NodeDefinition BodyWithInputs(string id, string typeKey, string parentId, string inputsJson) => new() { Id = id, TypeKey = typeKey, ParentId = parentId, Config = Empty(), Inputs = JsonDocument.Parse(inputsJson).RootElement.Clone() };

    private sealed class MapStubNode : INodeRuntime
    {
        public MapStubNode(string typeKey, NodeKind kind, bool canSuspend = false)
        {
            TypeKey = typeKey;
            Manifest = new NodeManifest
            {
                DisplayName = typeKey,
                Category = "Test",
                Kind = kind,
                CanSuspend = canSuspend,
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
