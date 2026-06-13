using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// <c>llm.complete</c> — pins the text path (regression) and the new structured path: when a
/// <c>responseSchema</c> config is present the node routes through the <see cref="IStructuredLLMClient"/>
/// sibling capability and surfaces the parsed object on the <c>json</c> output (so downstream can index
/// into it). A provider that doesn't implement the sibling fails cleanly rather than silently degrading.
/// </summary>
[Trait("Category", "Unit")]
public class LlmCompleteNodeTests
{
    /// <summary>Implements BOTH client interfaces — the Anthropic shape.</summary>
    private sealed class StructuredStubClient : ILLMClient, IStructuredLLMClient
    {
        public string Provider => "Anthropic";
        public StructuredLLMCompletionRequest? StructuredRequest;
        public LLMCompletion TextResult = new() { Text = "plain answer", Model = "claude-sonnet-4-5", InputTokens = 10, OutputTokens = 5 };
        public StructuredLLMCompletion StructuredResult = new()
        {
            Json = JsonDocument.Parse("""{ "subtasks": ["a", "b"] }""").RootElement.Clone(),
            Model = "claude-sonnet-4-5", InputTokens = 11, OutputTokens = 6
        };

        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken ct) => Task.FromResult(TextResult);

        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken ct)
        {
            StructuredRequest = request;
            return Task.FromResult(StructuredResult);
        }
    }

    /// <summary>Text-only provider — does NOT implement IStructuredLLMClient.</summary>
    private sealed class TextOnlyStubClient : ILLMClient
    {
        public string Provider => "Anthropic";
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken ct) =>
            Task.FromResult(new LLMCompletion { Text = "text", Model = "m", InputTokens = 1, OutputTokens = 1 });
    }

    private sealed class StubRegistry : ILLMClientRegistry
    {
        private readonly ILLMClient _client;
        public StubRegistry(ILLMClient client) { _client = client; }
        public ILLMClient Resolve(string provider) => _client;
        public IReadOnlyList<ILLMClient> All => new[] { _client };
    }

    [Fact]
    public async Task Text_path_outputs_completion_text_and_null_json()
    {
        var node = new LlmCompleteNode(new StubRegistry(new StructuredStubClient()));

        var result = await node.RunAsync(Context(config: new(), userPrompt: "hi"), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        result.Outputs["text"].GetString().ShouldBe("plain answer");
        result.Outputs["json"].ValueKind.ShouldBe(JsonValueKind.Null, "no schema → text mode, json output is null");
    }

    [Fact]
    public async Task Structured_path_surfaces_the_parsed_object_on_json()
    {
        var stub = new StructuredStubClient();
        var node = new LlmCompleteNode(new StubRegistry(stub));

        var config = new Dictionary<string, JsonElement>
        {
            ["provider"] = JsonSerializer.SerializeToElement("Anthropic"),
            ["responseSchema"] = JsonDocument.Parse("""{ "type": "object", "properties": { "subtasks": { "type": "array" } } }""").RootElement.Clone(),
        };

        var result = await node.RunAsync(Context(config, userPrompt: "plan it"), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        stub.StructuredRequest.ShouldNotBeNull("the schema config must route through the structured client");
        stub.StructuredRequest!.JsonSchema.GetProperty("properties").GetProperty("subtasks").GetProperty("type").GetString().ShouldBe("array");

        // The parsed object is on `json` — and downstream can index into it (the flow.map bridge).
        result.Outputs["json"].GetProperty("subtasks")[0].GetString().ShouldBe("a");
        result.Outputs["json"].GetProperty("subtasks").GetArrayLength().ShouldBe(2);
        result.Outputs["text"].GetString().ShouldContain("subtasks", customMessage: "text stays populated with the serialized json for back-compat");
    }

    [Fact]
    public async Task Structured_path_fails_cleanly_when_provider_lacks_the_capability()
    {
        var node = new LlmCompleteNode(new StubRegistry(new TextOnlyStubClient()));

        var config = new Dictionary<string, JsonElement>
        {
            ["provider"] = JsonSerializer.SerializeToElement("Anthropic"),
            ["responseSchema"] = JsonDocument.Parse("""{ "type": "object" }""").RootElement.Clone(),
        };

        var result = await node.RunAsync(Context(config, userPrompt: "x"), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Failure);
        result.Error.ShouldContain("structured output");
    }

    [Fact]
    public async Task An_empty_response_schema_object_is_treated_as_text_mode()
    {
        var stub = new StructuredStubClient();
        var node = new LlmCompleteNode(new StubRegistry(stub));

        var config = new Dictionary<string, JsonElement>
        {
            ["provider"] = JsonSerializer.SerializeToElement("Anthropic"),
            ["responseSchema"] = JsonDocument.Parse("{}").RootElement.Clone(),
        };

        var result = await node.RunAsync(Context(config, userPrompt: "x"), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        stub.StructuredRequest.ShouldBeNull("an empty {} schema is not a real constraint — stay in text mode");
        result.Outputs["text"].GetString().ShouldBe("plain answer");
    }

    private static NodeRunContext Context(Dictionary<string, JsonElement> config, string userPrompt) => new()
    {
        Inputs = new Dictionary<string, JsonElement> { ["userPrompt"] = JsonSerializer.SerializeToElement(userPrompt) },
        Config = config,
        RawInputs = JsonDocument.Parse("{}").RootElement,
        RawConfig = JsonDocument.Parse("{}").RootElement,
        Scope = new NodeRunScope { Trigger = new Dictionary<string, JsonElement>() },
        Logger = NullLogger.Instance,
        Observability = NodeObservability.NoOp,
    };
}
