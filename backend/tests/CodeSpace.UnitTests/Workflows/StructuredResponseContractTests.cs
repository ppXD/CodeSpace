using System.Net;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Llm.Anthropic;
using CodeSpace.Core.Services.Workflows.Llm.OpenAi;
using CodeSpace.Messages.Agents;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Workflows.Planning.Planners;
using CodeSpace.Messages.Dtos.Workflows.Planning;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public sealed class StructuredResponseContractTests
{
    [Theory]
    [InlineData("Anthropic")]
    [InlineData("OpenAI")]
    public async Task A_consumer_contract_violation_reasks_the_model_and_preserves_its_exact_corrected_payload(string provider)
    {
        var handler = new WireHandler(provider, ["{}", """{"argv":["/bin/sh",""," ","oracle.sh"]}"""]);
        var response = await Client(provider, handler).CompleteStructuredAsync(Request(provider), CancellationToken.None);
        handler.Bodies.Count.ShouldBe(2);
        handler.Bodies[1].ShouldContain("consumer requires non-empty argv");
        response.Json.GetProperty("argv").EnumerateArray().Select(x => x.GetString()).ToArray().ShouldBe(new[] { "/bin/sh", "", " ", "oracle.sh" });
        response.Usage.InputTokens.ShouldBe(24);
        response.Usage.OutputTokens.ShouldBe(14);
        response.Usage.IsPartial.ShouldBeFalse();
    }

    [Theory]
    [InlineData("Anthropic")]
    [InlineData("OpenAI")]
    public async Task Repeated_consumer_contract_failure_is_typed_and_bounded(string provider)
    {
        var handler = new WireHandler(provider, ["{}", "{}"]);
        var error = await Should.ThrowAsync<LlmApiException>(() => Client(provider, handler).CompleteStructuredAsync(Request(provider), CancellationToken.None));
        error.Category.ShouldBe(LlmErrorCategory.Malformed);
        error.Message.ShouldContain("consumer requires non-empty argv");
        handler.Bodies.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("Anthropic")]
    [InlineData("OpenAI")]
    public async Task A_valid_response_uses_one_call_and_the_validator_is_never_serialized(string provider)
    {
        var request = Request(provider);
        JsonSerializer.Serialize(request).ShouldNotContain("ResponseValidator");
        var handler = new WireHandler(provider, ["""{"argv":["arbitrary-executable",""]}"""]);
        (await Client(provider, handler).CompleteStructuredAsync(request, CancellationToken.None)).Json.GetProperty("argv")[1].GetString().ShouldBe("");
        handler.Bodies.Count.ShouldBe(1);
        handler.Bodies[0].ShouldNotContain("ResponseValidator");
    }

    [Theory]
    [InlineData("Anthropic")]
    [InlineData("OpenAI")]
    public async Task Cancellation_from_the_consumer_does_not_trigger_another_model_call(string provider)
    {
        var handler = new WireHandler(provider, ["{}"]);
        var request = Request(provider) with { ResponseValidator = _ => throw new OperationCanceledException() };
        await Should.ThrowAsync<OperationCanceledException>(() => Client(provider, handler).CompleteStructuredAsync(request, CancellationToken.None));
        handler.Bodies.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("Anthropic", "TestsPass", "argv")]
    [InlineData("OpenAI", "TestsPass", "argv")]
    [InlineData("Anthropic", "ArtifactPresent", "artifactPaths")]
    [InlineData("OpenAI", "ArtifactPresent", "artifactPaths")]
    public async Task The_actual_planner_request_repairs_missing_oracle_payload_through_the_existing_provider_path(string provider, string kind, string payloadName)
    {
        var bad = "{\"goal\":\"produce the requested result\",\"subtasks\":[{\"id\":\"s1\",\"title\":\"result\",\"instruction\":\"do the work\",\"acceptance\":{\"formatVersion\":2,\"kind\":\"" + kind + "\"}}],\"successCriteria\":[],\"risks\":[],\"recommendedWorkflowKind\":\"coding\"}";
        var payload = kind == "TestsPass" ? "[\"arbitrary-command\",\"\",\" \"]" : "[\"chosen-result.bin\"]";
        var good = bad.Replace("\"kind\":\"" + kind + "\"", "\"kind\":\"" + kind + "\",\"" + payloadName + "\":" + payload);
        var request = LlmWorkflowPlanner.BuildRequest(new WorkflowPlanRequest { TaskText = "produce the result", TeamId = Guid.NewGuid() }, new ModelPoolPick { ModelId = "wire-test-model", Credential = new ResolvedModelCredential { Provider = provider, ApiKey = "fixture-key" } }, "", []);
        var handler = new WireHandler(provider, [bad, good]);
        var response = await Client(provider, handler).CompleteStructuredAsync(request, CancellationToken.None);
        handler.Bodies.Count.ShouldBe(2);
        handler.Bodies[1].ShouldContain(payloadName);
        var actual = LlmWorkflowPlanner.Deserialize(response.Json).Subtasks.Single().Acceptance.ShouldNotBeNull().Command;
        actual.ShouldBe(JsonDocument.Parse(payload).RootElement.EnumerateArray().Select(x => x.GetString()).ToArray());
        response.Usage.InputTokens.ShouldBe(24);
        response.Usage.OutputTokens.ShouldBe(14);
    }

    private static StructuredLLMCompletionRequest Request(string provider) => new()
    {
        Model = "wire-test-model", SystemPrompt = "Return data", UserPrompt = "Create the requested result", JsonSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement,
        Credential = new ResolvedModelCredential { Provider = provider, ApiKey = "fixture-key" },
        ResponseValidator = json => json.TryGetProperty("argv", out var argv) && argv.ValueKind == JsonValueKind.Array && argv.GetArrayLength() > 0 ? [] : ["consumer requires non-empty argv"],
    };

    private static IStructuredLLMClient Client(string provider, WireHandler handler) => provider == "Anthropic" ? new AnthropicClient(new Factory(handler)) : new OpenAiClient(new Factory(handler));
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
    private sealed class WireHandler(string provider, string[] responses) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var content = responses[Math.Min(Bodies.Count - 1, responses.Length - 1)];
            var response = provider == "Anthropic"
                ? "{\"model\":\"wire-test-model\",\"content\":[{\"type\":\"tool_use\",\"id\":\"call_fixture\",\"name\":\"respond\",\"input\":" + content + "}],\"usage\":{\"input_tokens\":12,\"output_tokens\":7}}"
                : "{\"model\":\"wire-test-model\",\"choices\":[{\"message\":{\"role\":\"assistant\",\"tool_calls\":[{\"id\":\"call_fixture\",\"type\":\"function\",\"function\":{\"name\":\"respond\",\"arguments\":" + JsonSerializer.Serialize(content) + "}}]},\"finish_reason\":\"tool_calls\"}],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":7}}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
