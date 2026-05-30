using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Validator is the single write-time gate keeping malformed workflows out of the DB.
/// Pin every check it makes — every error message corresponds to a definition shape an
/// operator might author. If we silently accept any of these, runtime explodes.
/// </summary>
[Trait("Category", "Unit")]
public class DefinitionValidatorTests
{
    private static DefinitionValidator BuildValidator()
    {
        var nodes = new INodeRuntime[]
        {
            new StubNode("trigger.x", NodeKind.Trigger),
            new StubNode("regular.a", NodeKind.Regular),
            new StubNode("regular.b", NodeKind.Regular),
            new StubNode("builtin.terminal", NodeKind.Terminal)
        };

        return new DefinitionValidator(new NodeRegistry(nodes));
    }

    [Fact]
    public void Linear_definition_passes()
    {
        var definition = new WorkflowDefinition
        {
            Nodes = new List<NodeDefinition>
            {
                Node("t", "trigger.x"),
                Node("a", "regular.a"),
                Node("end", "builtin.terminal")
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "t", To = "a" },
                new() { From = "a", To = "end" }
            }
        };

        BuildValidator().Validate(definition).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Unknown_typekey_errors()
    {
        var definition = MakeMinimalDefinition(extraNode: Node("missing", "no.such.node"));
        var result = BuildValidator().Validate(definition);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("no.such.node"));
    }

    [Fact]
    public void Cycle_errors()
    {
        // t → a → b → a (cycle through a-b)
        var definition = new WorkflowDefinition
        {
            Nodes = new List<NodeDefinition>
            {
                Node("t", "trigger.x"),
                Node("a", "regular.a"),
                Node("b", "regular.b"),
                Node("end", "builtin.terminal")
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "t", To = "a" },
                new() { From = "a", To = "b" },
                new() { From = "b", To = "a" },
                new() { From = "a", To = "end" }
            }
        };

        var result = BuildValidator().Validate(definition);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("cycle"));
    }

    [Fact]
    public void Missing_trigger_errors()
    {
        var definition = new WorkflowDefinition
        {
            Nodes = new List<NodeDefinition>
            {
                Node("a", "regular.a"),
                Node("end", "builtin.terminal")
            },
            Edges = new List<EdgeDefinition> { new() { From = "a", To = "end" } }
        };

        var result = BuildValidator().Validate(definition);
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("Trigger node"));
    }

    [Fact]
    public void Multiple_triggers_error()
    {
        var definition = new WorkflowDefinition
        {
            Nodes = new List<NodeDefinition>
            {
                Node("t1", "trigger.x"),
                Node("t2", "trigger.x"),
                Node("end", "builtin.terminal")
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "t1", To = "end" },
                new() { From = "t2", To = "end" }
            }
        };

        var result = BuildValidator().Validate(definition);
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("Trigger nodes"));
    }

    [Fact]
    public void Missing_terminal_errors()
    {
        var definition = new WorkflowDefinition
        {
            Nodes = new List<NodeDefinition>
            {
                Node("t", "trigger.x"),
                Node("a", "regular.a")
            },
            Edges = new List<EdgeDefinition> { new() { From = "t", To = "a" } }
        };

        var result = BuildValidator().Validate(definition);
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("Terminal"));
    }

    [Fact]
    public void Unreachable_node_errors()
    {
        // 'b' is in the graph but no edge reaches it.
        var definition = new WorkflowDefinition
        {
            Nodes = new List<NodeDefinition>
            {
                Node("t", "trigger.x"),
                Node("a", "regular.a"),
                Node("b", "regular.b"),
                Node("end", "builtin.terminal")
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "t", To = "a" },
                new() { From = "a", To = "end" }
            }
        };

        var result = BuildValidator().Validate(definition);
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("'b'") && e.Contains("not reachable"));
    }

    [Fact]
    public void Edge_to_unknown_node_errors()
    {
        var definition = new WorkflowDefinition
        {
            Nodes = new List<NodeDefinition>
            {
                Node("t", "trigger.x"),
                Node("end", "builtin.terminal")
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "t", To = "ghost" },
                new() { From = "ghost", To = "end" }
            }
        };

        var result = BuildValidator().Validate(definition);
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("'ghost'"));
    }

    [Fact]
    public void Duplicate_node_id_errors()
    {
        var definition = new WorkflowDefinition
        {
            Nodes = new List<NodeDefinition>
            {
                Node("t", "trigger.x"),
                Node("dup", "regular.a"),
                Node("dup", "regular.b"),
                Node("end", "builtin.terminal")
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "t", To = "dup" },
                new() { From = "dup", To = "end" }
            }
        };

        var result = BuildValidator().Validate(definition);
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("Duplicate"));
    }

    [Fact]
    public void Project_reference_with_slug_and_name_is_accepted()
    {
        var definition = new WorkflowDefinition
        {
            Nodes = new List<NodeDefinition>
            {
                Node("t", "trigger.x"),
                NodeWithInputs("a", "regular.a", """{"key":"{{project.shared.api_key}}"}"""),
                Node("end", "builtin.terminal")
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "t", To = "a" },
                new() { From = "a", To = "end" }
            }
        };

        BuildValidator().Validate(definition).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Project_reference_with_only_head_is_rejected()
    {
        var definition = new WorkflowDefinition
        {
            Nodes = new List<NodeDefinition>
            {
                Node("t", "trigger.x"),
                NodeWithInputs("a", "regular.a", """{"key":"{{project}}"}"""),
                Node("end", "builtin.terminal")
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "t", To = "a" },
                new() { From = "a", To = "end" }
            }
        };

        var result = BuildValidator().Validate(definition);
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("project.<slug>.<name>"));
    }

    [Fact]
    public void Wrong_schema_version_errors()
    {
        var definition = new WorkflowDefinition
        {
            SchemaVersion = 99,
            Nodes = new List<NodeDefinition>
            {
                Node("t", "trigger.x"),
                Node("end", "builtin.terminal")
            },
            Edges = new List<EdgeDefinition> { new() { From = "t", To = "end" } }
        };

        var result = BuildValidator().Validate(definition);
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("schemaVersion"));
    }

    // ─── Retry policy (Phase 2) ────────────────────────────────────────────────

    [Theory]
    [InlineData(1, 0)]                              // no-retry sentinel
    [InlineData(3, 5)]                              // typical
    [InlineData(RetryPlan.MaxAttemptsCap, 0)]       // upper attempts boundary
    [InlineData(2, RetryPlan.MaxBackoffSeconds)]    // upper backoff boundary
    public void Valid_retry_policy_passes(int maxAttempts, double backoffSeconds)
    {
        var definition = MakeMinimalDefinition(extraNode: NodeWithRetry("a", "regular.a", maxAttempts, backoffSeconds));

        BuildValidator().Validate(definition).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]                                 // below floor
    [InlineData(-1)]                                // negative
    [InlineData(RetryPlan.MaxAttemptsCap + 1)]      // above cap
    public void Out_of_range_retry_maxAttempts_errors(int maxAttempts)
    {
        var definition = MakeMinimalDefinition(extraNode: NodeWithRetry("a", "regular.a", maxAttempts, 0));
        var result = BuildValidator().Validate(definition);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("maxAttempts") && e.Contains("'a'"));
    }

    [Theory]
    [InlineData(-1)]                                // negative
    [InlineData(RetryPlan.MaxBackoffSeconds + 1)]   // above cap
    public void Out_of_range_retry_backoff_errors(double backoffSeconds)
    {
        var definition = MakeMinimalDefinition(extraNode: NodeWithRetry("a", "regular.a", 3, backoffSeconds));
        var result = BuildValidator().Validate(definition);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("backoffSeconds") && e.Contains("'a'"));
    }

    [Fact]
    public void No_retry_policy_is_valid()
    {
        // The common case: a node with Retry == null must validate cleanly (non-breaking).
        var definition = MakeMinimalDefinition(extraNode: Node("a", "regular.a"));

        var aNode = definition.Nodes.Single(n => n.Id == "a");
        aNode.Retry.ShouldBeNull();
        BuildValidator().Validate(definition).IsValid.ShouldBeTrue();
    }

    private static WorkflowDefinition MakeMinimalDefinition(NodeDefinition? extraNode = null)
    {
        var nodes = new List<NodeDefinition>
        {
            Node("t", "trigger.x"),
            Node("end", "builtin.terminal")
        };

        var edges = new List<EdgeDefinition> { new() { From = "t", To = "end" } };

        if (extraNode != null)
        {
            nodes.Add(extraNode);
            edges.Add(new EdgeDefinition { From = "t", To = extraNode.Id });
            edges.Add(new EdgeDefinition { From = extraNode.Id, To = "end" });
        }

        return new WorkflowDefinition { Nodes = nodes, Edges = edges };
    }

    private static NodeDefinition Node(string id, string typeKey) => new()
    {
        Id = id,
        TypeKey = typeKey,
        Config = JsonDocument.Parse("{}").RootElement.Clone(),
        Inputs = JsonDocument.Parse("{}").RootElement.Clone()
    };

    private static NodeDefinition NodeWithInputs(string id, string typeKey, string inputsJson) => new()
    {
        Id = id,
        TypeKey = typeKey,
        Config = JsonDocument.Parse("{}").RootElement.Clone(),
        Inputs = JsonDocument.Parse(inputsJson).RootElement.Clone()
    };

    private static NodeDefinition NodeWithRetry(string id, string typeKey, int maxAttempts, double backoffSeconds) => new()
    {
        Id = id,
        TypeKey = typeKey,
        Config = JsonDocument.Parse("{}").RootElement.Clone(),
        Inputs = JsonDocument.Parse("{}").RootElement.Clone(),
        Retry = new RetryPolicy { MaxAttempts = maxAttempts, BackoffSeconds = backoffSeconds }
    };

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
                OutputSchema = SchemaBuilder.EmptyObject()
            };
        }

        public string TypeKey { get; }
        public NodeManifest Manifest { get; }
        public Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken) => Task.FromResult(NodeResult.Ok());
    }
}
