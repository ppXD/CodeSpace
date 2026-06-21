using System.Net;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Llm.OpenAi;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the OpenAI-compatible WIRE SHAPE without a live API: a captured <see cref="HttpMessageHandler"/> asserts
/// the structured request forces a single function whose <c>parameters</c> is the caller's schema (tool_choice
/// pinned to it), the response's <c>tool_calls[0].function.arguments</c> string is parsed as the JSON, the call
/// authenticates with the credential's <c>Bearer</c> key against its base URL, and a missing credential fails
/// closed. This is the riskiest part of a new provider — a drift in the Chat Completions function-calling contract
/// would silently break every structured run routed at an OpenAI-wire gateway.
/// </summary>
[Trait("Category", "Unit")]
public class OpenAiClientStructuredTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? RequestBody;
        public string? AuthorizationHeader;
        public string? RequestUri;
        public string? ContentType;
        public bool Sent;
        private readonly string _responseJson;
        private readonly HttpStatusCode _status;
        public CapturingHandler(string responseJson, HttpStatusCode status = HttpStatusCode.OK) { _responseJson = responseJson; _status = status; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent = true;
            AuthorizationHeader = request.Headers.Authorization?.ToString();
            RequestUri = request.RequestUri?.ToString();
            ContentType = request.Content?.Headers.ContentType?.ToString();
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_status) { Content = new StringContent(_responseJson, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubHttpClientFactory(HttpMessageHandler handler) { _handler = handler; }
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static readonly ResolvedModelCredential TestCredential = new() { Provider = "OpenAI", ApiKey = "test-key", BaseUrl = "https://gw.example.com/v1" };

    private static StructuredLLMCompletionRequest StructuredRequest(JsonElement schema, ResolvedModelCredential? credential) => new()
    {
        Model = "gpt-4o", SystemPrompt = "sys", UserPrompt = "decompose this", JsonSchema = schema, Credential = credential,
    };

    [Fact]
    public async Task Structured_completion_forces_a_function_and_extracts_its_arguments()
    {
        // The function arguments arrive as a JSON STRING (the OpenAI wire) — the client parses it into the JSON object.
        const string response = """
            {
              "model": "gpt-4o",
              "choices": [ { "message": { "role": "assistant", "content": null,
                "tool_calls": [ { "id": "call_1", "type": "function", "function": { "name": "respond", "arguments": "{\"subtasks\":[\"x\",\"y\"]}" } } ] } } ],
              "usage": { "prompt_tokens": 12, "completion_tokens": 7 }
            }
            """;
        var handler = new CapturingHandler(response);
        var client = new OpenAiClient(new StubHttpClientFactory(handler));
        var schema = JsonDocument.Parse("""{ "type": "object", "properties": { "subtasks": { "type": "array" } } }""").RootElement;

        var result = await client.CompleteStructuredAsync(StructuredRequest(schema, TestCredential), CancellationToken.None);

        // Response extraction: tool_calls[0].function.arguments became the JSON.
        result.Json.GetProperty("subtasks")[0].GetString().ShouldBe("x");
        result.Json.GetProperty("subtasks").GetArrayLength().ShouldBe(2);
        result.Model.ShouldBe("gpt-4o");
        result.InputTokens.ShouldBe(12);
        result.OutputTokens.ShouldBe(7);

        // Request shape: a single forced function whose parameters IS the caller's schema, tool_choice pinned to it.
        var sent = JsonDocument.Parse(handler.RequestBody!).RootElement;
        var fn = sent.GetProperty("tools")[0].GetProperty("function");
        fn.GetProperty("name").GetString().ShouldBe("respond");
        fn.GetProperty("parameters").GetProperty("properties").GetProperty("subtasks").GetProperty("type").GetString().ShouldBe("array");
        sent.GetProperty("tool_choice").GetProperty("type").GetString().ShouldBe("function");
        sent.GetProperty("tool_choice").GetProperty("function").GetProperty("name").GetString().ShouldBe("respond");
        sent.GetProperty("messages")[1].GetProperty("content").GetString().ShouldBe("decompose this");
        sent.GetProperty("messages")[0].GetProperty("role").GetString().ShouldBe("system");
    }

    [Fact]
    public async Task Structured_completion_throws_when_there_is_neither_a_tool_call_nor_json_content()
    {
        // Model answered with plain prose (no function call, no JSON) — surface it, don't return garbage.
        const string response = """{ "model": "m", "choices": [ { "message": { "role": "assistant", "content": "I cannot." } } ] }""";
        var client = new OpenAiClient(new StubHttpClientFactory(new CapturingHandler(response)));
        var schema = JsonDocument.Parse("""{ "type": "object" }""").RootElement;

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => client.CompleteStructuredAsync(StructuredRequest(schema, TestCredential), CancellationToken.None));

        ex.Message.ShouldContain("did not produce structured output");
    }

    [Fact]
    public async Task Structured_completion_falls_back_to_json_in_the_content_when_the_model_skips_the_function()
    {
        // The real-endpoint case: the model IGNORED the forced function and returned the JSON as fenced content. Recover it.
        var inner = "```json\n{\"subtasks\":[\"a\",\"b\"]}\n```";
        var response = JsonSerializer.Serialize(new { model = "m", choices = new[] { new { message = new { content = inner } } } });
        var client = new OpenAiClient(new StubHttpClientFactory(new CapturingHandler(response)));
        var schema = JsonDocument.Parse("""{ "type": "object" }""").RootElement;

        var result = await client.CompleteStructuredAsync(StructuredRequest(schema, TestCredential), CancellationToken.None);

        result.Json.GetProperty("subtasks")[0].GetString().ShouldBe("a");
        result.Json.GetProperty("subtasks").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task The_credential_bearer_key_and_base_url_authenticate_the_call_and_append_chat_completions()
    {
        const string response = """{ "model": "m", "choices": [ { "message": { "tool_calls": [ { "function": { "name": "respond", "arguments": "{}" } } ] } } ] }""";
        var handler = new CapturingHandler(response);
        var client = new OpenAiClient(new StubHttpClientFactory(handler));
        var schema = JsonDocument.Parse("""{ "type": "object" }""").RootElement;

        // A trailing slash on the base URL is tolerated; the client appends /chat/completions exactly once.
        await client.CompleteStructuredAsync(StructuredRequest(schema, new ResolvedModelCredential { Provider = "OpenAI", ApiKey = "team-key", BaseUrl = "https://team.gateway/v1/" }), CancellationToken.None);

        handler.AuthorizationHeader.ShouldBe("Bearer team-key", "the resolved credential's key is the Bearer token");
        handler.RequestUri.ShouldBe("https://team.gateway/v1/chat/completions", "base URL + /chat/completions, trailing slash collapsed");
        handler.ContentType.ShouldBe("application/json", "no `; charset=utf-8` suffix — strict gateways reject it");
    }

    [Fact]
    public async Task Structured_completion_accepts_function_arguments_as_an_inline_object()
    {
        // Some OpenAI-compatible gateways return `arguments` as an inline OBJECT rather than a JSON string — accept both,
        // or a real custom-endpoint run silently dies on response deserialization (the fake-HTTP tests miss it otherwise).
        const string response = """{ "model": "m", "choices": [ { "message": { "tool_calls": [ { "function": { "name": "respond", "arguments": { "subtasks": ["a", "b"] } } } ] } } ] }""";
        var client = new OpenAiClient(new StubHttpClientFactory(new CapturingHandler(response)));
        var schema = JsonDocument.Parse("""{ "type": "object" }""").RootElement;

        var result = await client.CompleteStructuredAsync(StructuredRequest(schema, TestCredential), CancellationToken.None);

        result.Json.GetProperty("subtasks")[0].GetString().ShouldBe("a");
        result.Json.GetProperty("subtasks").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task Without_a_credential_the_call_fails_closed_before_any_request()
    {
        var handler = new CapturingHandler("""{}""");
        var client = new OpenAiClient(new StubHttpClientFactory(handler));
        var schema = JsonDocument.Parse("""{ "type": "object" }""").RootElement;

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => client.CompleteStructuredAsync(StructuredRequest(schema, credential: null), CancellationToken.None));

        ex.Message.ShouldContain("API key not configured");
        handler.Sent.ShouldBeFalse("the call must fail closed before any request is sent");
    }

    [Fact]
    public async Task Free_text_completion_returns_the_message_content()
    {
        const string response = """{ "model": "gpt-4o", "choices": [ { "message": { "role": "assistant", "content": "hello there" } } ], "usage": { "prompt_tokens": 3, "completion_tokens": 2 } }""";
        var client = new OpenAiClient(new StubHttpClientFactory(new CapturingHandler(response)));

        var result = await client.CompleteAsync(new LLMCompletionRequest { Model = "gpt-4o", SystemPrompt = "s", UserPrompt = "u", Credential = TestCredential }, CancellationToken.None);

        result.Text.ShouldBe("hello there");
        result.InputTokens.ShouldBe(3);
        result.OutputTokens.ShouldBe(2);
    }

    [Fact]
    public async Task A_non_success_status_throws_with_the_body()
    {
        var handler = new CapturingHandler("""{ "error": { "message": "bad model" } }""", HttpStatusCode.BadRequest);
        var client = new OpenAiClient(new StubHttpClientFactory(handler));
        var schema = JsonDocument.Parse("""{ "type": "object" }""").RootElement;

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => client.CompleteStructuredAsync(StructuredRequest(schema, TestCredential), CancellationToken.None));

        ex.Message.ShouldContain("400");
        ex.Message.ShouldContain("bad model");
    }

    [Fact]
    public void The_module_registers_the_openai_provider()
    {
        var module = new OpenAiLlmProviderModule();

        module.Provider.ShouldBe("OpenAI");
        module.Client.ShouldBe(typeof(OpenAiClient));
        new OpenAiClient(new StubHttpClientFactory(new CapturingHandler("{}"))).Provider.ShouldBe("OpenAI", "the client's provider tag matches so the registry resolves it for an OpenAI credential");
    }
}
