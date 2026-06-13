using System.Net;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Llm.Anthropic;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the Anthropic structured-output WIRE SHAPE without a live API: a captured
/// <see cref="HttpMessageHandler"/> asserts the request forces a single tool whose input_schema is the
/// caller's schema (tool_choice pinned to it), and that the response's <c>tool_use.input</c> block is
/// extracted as the JSON. This is the riskiest part of the structured path — a drift in the Messages
/// tool-use contract would silently break every <c>llm.complete</c> structured run.
/// </summary>
[Trait("Category", "Unit")]
public class AnthropicClientStructuredTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? RequestBody;
        private readonly string _responseJson;
        public CapturingHandler(string responseJson) { _responseJson = responseJson; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_responseJson, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubHttpClientFactory(HttpMessageHandler handler) { _handler = handler; }
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    [Fact]
    public async Task Structured_completion_forces_a_tool_and_extracts_its_input()
    {
        const string response = """
            {
              "model": "claude-sonnet-4-5",
              "content": [
                { "type": "tool_use", "id": "toolu_1", "name": "respond", "input": { "subtasks": ["x", "y"] } }
              ],
              "usage": { "input_tokens": 12, "output_tokens": 7 }
            }
            """;
        var handler = new CapturingHandler(response);
        var client = new AnthropicClient(new StubHttpClientFactory(handler));

        var schema = JsonDocument.Parse("""{ "type": "object", "properties": { "subtasks": { "type": "array" } } }""").RootElement;

        var (json, model, inTok, outTok) = await WithApiKeyAsync(async () =>
        {
            var result = await client.CompleteStructuredAsync(new StructuredLLMCompletionRequest
            {
                Model = "claude-sonnet-4-5", SystemPrompt = "sys", UserPrompt = "decompose this", JsonSchema = schema,
            }, CancellationToken.None);
            return (result.Json, result.Model, result.InputTokens, result.OutputTokens);
        });

        // Response extraction: tool_use.input became the JSON.
        json.GetProperty("subtasks")[0].GetString().ShouldBe("x");
        json.GetProperty("subtasks").GetArrayLength().ShouldBe(2);
        model.ShouldBe("claude-sonnet-4-5");
        inTok.ShouldBe(12);
        outTok.ShouldBe(7);

        // Request shape: a single forced tool whose input_schema IS the caller's schema.
        var sent = JsonDocument.Parse(handler.RequestBody!).RootElement;
        var tool = sent.GetProperty("tools")[0];
        tool.GetProperty("name").GetString().ShouldBe("respond");
        tool.GetProperty("input_schema").GetProperty("properties").GetProperty("subtasks").GetProperty("type").GetString().ShouldBe("array");
        sent.GetProperty("tool_choice").GetProperty("type").GetString().ShouldBe("tool");
        sent.GetProperty("tool_choice").GetProperty("name").GetString().ShouldBe("respond");
        sent.GetProperty("messages")[0].GetProperty("content").GetString().ShouldBe("decompose this");
    }

    [Fact]
    public async Task Structured_completion_throws_when_no_tool_use_block_is_returned()
    {
        // Model answered with plain text instead of calling the tool — surface it, don't return garbage.
        const string response = """{ "model": "m", "content": [ { "type": "text", "text": "I cannot." } ], "usage": { "input_tokens": 1, "output_tokens": 1 } }""";
        var client = new AnthropicClient(new StubHttpClientFactory(new CapturingHandler(response)));
        var schema = JsonDocument.Parse("""{ "type": "object" }""").RootElement;

        await WithApiKeyAsync(async () =>
        {
            var ex = await Should.ThrowAsync<InvalidOperationException>(() => client.CompleteStructuredAsync(new StructuredLLMCompletionRequest
            {
                Model = "m", SystemPrompt = "", UserPrompt = "x", JsonSchema = schema,
            }, CancellationToken.None));
            ex.Message.ShouldContain("tool_use");
            return true;
        });
    }

    /// <summary>Sets the API-key + base-url env vars for the duration of the call, then restores them.</summary>
    private static async Task<T> WithApiKeyAsync<T>(Func<Task<T>> action)
    {
        var oldKey = Environment.GetEnvironmentVariable(AnthropicClient.ApiKeyEnvVar);
        var oldUrl = Environment.GetEnvironmentVariable(AnthropicClient.ApiBaseUrlEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(AnthropicClient.ApiKeyEnvVar, "test-key");
            Environment.SetEnvironmentVariable(AnthropicClient.ApiBaseUrlEnvVar, "https://test.invalid");
            return await action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(AnthropicClient.ApiKeyEnvVar, oldKey);
            Environment.SetEnvironmentVariable(AnthropicClient.ApiBaseUrlEnvVar, oldUrl);
        }
    }
}
