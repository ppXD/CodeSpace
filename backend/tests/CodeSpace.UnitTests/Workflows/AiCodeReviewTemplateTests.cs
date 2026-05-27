using System.Reflection;
using System.Text.Json;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The AI Code Review template is the canonical built-in workflow. If its JSON drifts off
/// the engine's schema, the operator's "click Create from template" flow breaks. Pin it.
/// </summary>
[Trait("Category", "Unit")]
public class AiCodeReviewTemplateTests
{
    private const string TemplatePath = "../../../../../templates/ai_code_review.json";

    [Fact]
    public void Template_file_exists()
    {
        var resolved = ResolveTemplatePath();
        File.Exists(resolved).ShouldBeTrue($"Template file should exist at '{resolved}'.");
    }

    [Fact]
    public void Template_loads_into_known_dto_shape()
    {
        var json = File.ReadAllText(ResolveTemplatePath());
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("name").GetString().ShouldBe("AI Code Review");
        doc.RootElement.GetProperty("definition").GetProperty("schemaVersion").GetInt32().ShouldBe(WorkflowDefinition.CurrentSchemaVersion);
    }

    [Fact]
    public void Template_definition_passes_validator()
    {
        var json = File.ReadAllText(ResolveTemplatePath());
        var doc = JsonDocument.Parse(json);
        var definitionElement = doc.RootElement.GetProperty("definition");
        var definition = JsonSerializer.Deserialize<WorkflowDefinition>(definitionElement.GetRawText(), WorkflowJson.Options)!;

        var nodes = new INodeRuntime[]
        {
            new TriggerPrOpenedNode(),
            new TriggerPrUpdatedNode(),
            new TerminalNode(),
            new StubNode("git.fetch_pr_diff", NodeKind.Regular),
            new StubNode("git.post_pr_comment", NodeKind.Regular),
            new StubNode("llm.complete", NodeKind.Regular)
        };

        var validator = new DefinitionValidator(new NodeRegistry(nodes));
        var result = validator.Validate(definition);

        if (!result.IsValid)
            throw new Exception("Template validation failed: " + string.Join("; ", result.Errors));
    }

    private static string ResolveTemplatePath()
    {
        var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        return Path.GetFullPath(Path.Combine(dir, TemplatePath));
    }

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
