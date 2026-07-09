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

    /// <summary>Returns a different canned response per call, capturing each request body — lets a test prove the progressive fallback fires (attempt 1 then attempt 2 hit the endpoint twice with different shapes).</summary>
    private sealed class SequencedHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses;
        public readonly List<string> RequestBodies = new();
        public SequencedHandler(params (HttpStatusCode Status, string Body)[] responses) { _responses = new Queue<(HttpStatusCode, string)>(responses); }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            var (status, body) = _responses.Count > 0 ? _responses.Dequeue() : (HttpStatusCode.OK, "{}");
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
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
                "tool_calls": [ { "id": "call_1", "type": "function", "function": { "name": "respond", "arguments": "{\"subtasks\":[\"x\",\"y\"]}" } } ] }, "finish_reason": "tool_calls" } ],
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
        result.Usage.InputTokens.ShouldBe(12);
        result.Usage.OutputTokens.ShouldBe(7);
        result.Usage.FinishReason.ShouldBe("tool_calls", "the OpenAI finish_reason is surfaced on the usage envelope");

        // Request shape of attempt 1: a single forced function whose parameters IS the caller's schema, tool_choice
        // pinned to it. response_format is NOT sent (it 400s on gateways that do not support json_object).
        var sent = JsonDocument.Parse(handler.RequestBody!).RootElement;
        var fn = sent.GetProperty("tools")[0].GetProperty("function");
        fn.GetProperty("name").GetString().ShouldBe("respond");
        fn.GetProperty("parameters").GetProperty("properties").GetProperty("subtasks").GetProperty("type").GetString().ShouldBe("array");
        sent.GetProperty("tool_choice").GetProperty("type").GetString().ShouldBe("function");
        sent.GetProperty("tool_choice").GetProperty("function").GetProperty("name").GetString().ShouldBe("respond");
        sent.TryGetProperty("response_format", out _).ShouldBeFalse("response_format is omitted — json_object 400s on gateways that don't support it");
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

        // A model that yields no structured output is a typed Malformed capability fault (so the supervisor decider can
        // fail it closed to a clean stop rather than crash the run), not a bare InvalidOperationException.
        var ex = await Should.ThrowAsync<LlmApiException>(() => client.CompleteStructuredAsync(StructuredRequest(schema, TestCredential), CancellationToken.None));

        ex.Category.ShouldBe(LlmErrorCategory.Malformed);
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
    public async Task Degrades_to_a_prompt_only_request_when_the_forced_function_is_rejected_with_a_400()
    {
        // A LiteLLM-style gateway 400s the forced-function request (the feature is unsupported), then answers the
        // prompt-only retry in plain content. The client must NOT surface the 400 — it must degrade and recover.
        var handler = new SequencedHandler(
            (HttpStatusCode.BadRequest, """{ "error": { "message": "tool_choice is not supported by this model" } }"""),
            (HttpStatusCode.OK, """{ "model": "m", "choices": [ { "message": { "content": "{\"subtasks\":[\"a\",\"b\"]}" } } ] }"""));
        var client = new OpenAiClient(new StubHttpClientFactory(handler));
        var schema = JsonDocument.Parse("""{ "type": "object" }""").RootElement;

        var result = await client.CompleteStructuredAsync(StructuredRequest(schema, TestCredential), CancellationToken.None);

        result.Json.GetProperty("subtasks")[0].GetString().ShouldBe("a");
        handler.RequestBodies.Count.ShouldBe(2, "attempt 1 (forced function) then attempt 2 (prompt-only) both hit the endpoint");
        JsonDocument.Parse(handler.RequestBodies[0]).RootElement.TryGetProperty("tool_choice", out _).ShouldBeTrue("attempt 1 forces the function");
        JsonDocument.Parse(handler.RequestBodies[1]).RootElement.TryGetProperty("tools", out _).ShouldBeFalse("attempt 2 sends NO tools — the safest request");
    }

    [Fact]
    public async Task Degrades_to_a_prompt_only_request_when_the_forced_function_returns_no_usable_output()
    {
        // The model honours neither the forced function nor a JSON content object on attempt 1 (e.g. an empty/prose
        // reply), but answers the prompt-only retry. The client recovers from attempt 2.
        var handler = new SequencedHandler(
            (HttpStatusCode.OK, """{ "model": "m", "choices": [ { "message": { "content": "" } } ] }"""),
            (HttpStatusCode.OK, """{ "model": "m", "choices": [ { "message": { "content": "```json\n{\"kind\":\"plan\"}\n```" } } ] }"""));
        var client = new OpenAiClient(new StubHttpClientFactory(handler));
        var schema = JsonDocument.Parse("""{ "type": "object" }""").RootElement;

        var result = await client.CompleteStructuredAsync(StructuredRequest(schema, TestCredential), CancellationToken.None);

        result.Json.GetProperty("kind").GetString().ShouldBe("plan");
        handler.RequestBodies.Count.ShouldBe(2, "attempt 1 produced no usable output, so the prompt-only floor ran");
    }

    [Fact]
    public async Task Recovers_a_TRUNCATED_structured_reply_by_repairing_it_instead_of_faulting()
    {
        // The forced function is unsupported (400), and the prompt-only floor returns JSON that was CUT OFF at the token
        // cap — a long decision truncated mid-string. Before repair this threw a Malformed fault and lost the whole
        // decision; now the client closes the object + recovers the completed leading fields.
        var handler = new SequencedHandler(
            (HttpStatusCode.BadRequest, """{ "error": { "message": "tool_choice unsupported" } }"""),
            (HttpStatusCode.OK, """{ "model": "m", "choices": [ { "message": { "content": "{\"kind\":\"stop\",\"rationale\":{\"why\":\"the work is fully complete and verified" } } ] }"""));
        var client = new OpenAiClient(new StubHttpClientFactory(handler));
        var schema = JsonDocument.Parse("""{ "type": "object" }""").RootElement;

        var result = await client.CompleteStructuredAsync(StructuredRequest(schema, TestCredential), CancellationToken.None);

        result.Json.GetProperty("kind").GetString().ShouldBe("stop", "a truncated decision is repaired + recovered, not lost to a Malformed fault");
        result.Json.GetProperty("rationale").GetProperty("why").GetString().ShouldStartWith("the work is fully");
    }

    [Fact]
    public async Task Usage_totals_the_billed_forced_attempt_plus_the_floor_when_it_degrades()
    {
        // The forced-function attempt is a 200 with usage (BILLED) but yields no usable output → degrades to the floor,
        // also billed. The returned usage must TOTAL both POSTs, not just the floor.
        var handler = new SequencedHandler(
            (HttpStatusCode.OK, """{ "model": "m", "choices": [ { "message": { "content": "" }, "finish_reason": "tool_calls" } ], "usage": { "prompt_tokens": 12, "completion_tokens": 7 } }"""),
            (HttpStatusCode.OK, """{ "model": "m", "choices": [ { "message": { "content": "{\"kind\":\"plan\"}" }, "finish_reason": "stop" } ], "usage": { "prompt_tokens": 8, "completion_tokens": 5 } }"""));
        var client = new OpenAiClient(new StubHttpClientFactory(handler));
        var schema = JsonDocument.Parse("""{ "type": "object" }""").RootElement;

        var result = await client.CompleteStructuredAsync(StructuredRequest(schema, TestCredential), CancellationToken.None);

        result.Json.GetProperty("kind").GetString().ShouldBe("plan");
        result.Usage.InputTokens.ShouldBe(20, "the billed forced attempt (12) + the floor (8) — never just the floor");
        result.Usage.OutputTokens.ShouldBe(12, "7 + 5");
        result.Usage.FinishReason.ShouldBe("stop", "the accepted floor's reason, not the degraded forced attempt's 'tool_calls'");
    }

    [Fact]
    public async Task Usage_accumulates_across_a_schema_invalid_attempt_and_the_re_ask()
    {
        // Attempt 1's forced function returns {} — a valid JSON object but schema-INVALID (missing the required "kind").
        // That's billed (10/6); the client re-asks, also billed (11/7). The returned usage must TOTAL both — a re-asked
        // structured call bills twice, and reporting only the second under-counts the spend.
        var schema = JsonDocument.Parse("""{ "type": "object", "required": ["kind"], "properties": { "kind": { "type": "string" } } }""").RootElement;
        var handler = new SequencedHandler(
            (HttpStatusCode.OK, """{ "model": "m", "choices": [ { "message": { "tool_calls": [ { "function": { "name": "respond", "arguments": "{}" } } ] }, "finish_reason": "tool_calls" } ], "usage": { "prompt_tokens": 10, "completion_tokens": 6 } }"""),
            (HttpStatusCode.OK, """{ "model": "m", "choices": [ { "message": { "tool_calls": [ { "function": { "name": "respond", "arguments": "{\"kind\":\"plan\"}" } } ] }, "finish_reason": "tool_calls" } ], "usage": { "prompt_tokens": 11, "completion_tokens": 7 } }"""));
        var client = new OpenAiClient(new StubHttpClientFactory(handler));

        var result = await client.CompleteStructuredAsync(StructuredRequest(schema, TestCredential), CancellationToken.None);

        result.Json.GetProperty("kind").GetString().ShouldBe("plan");
        handler.RequestBodies.Count.ShouldBe(2, "a schema-invalid attempt 1 forces one re-ask");
        result.Usage.InputTokens.ShouldBe(21, "attempt 1 (10) + the re-ask (11) — a re-asked structured call bills twice");
        result.Usage.OutputTokens.ShouldBe(13, "6 + 7");
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

        ex.Message.ShouldContain("credential not configured", Case.Insensitive, "a NULL credential is a caller bug — fail closed (the KEY is optional, but the credential is required)");
        handler.Sent.ShouldBeFalse("the call must fail closed before any request is sent");
    }

    [Fact]
    public async Task A_keyless_credential_sends_no_authorization_header_so_a_local_gateway_runs()
    {
        // A keyless local / no-auth gateway (vLLM, a self-hosted relay): the credential has a base URL but no key, so the
        // call goes through with NO Authorization header — the in-process plane runs on it, not just the agent harness.
        const string response = """
            { "model": "m", "choices": [ { "message": { "role": "assistant", "content": null,
              "tool_calls": [ { "id": "c", "type": "function", "function": { "name": "respond", "arguments": "{\"ok\":true}" } } ] }, "finish_reason": "tool_calls" } ] }
            """;
        var handler = new CapturingHandler(response);
        var client = new OpenAiClient(new StubHttpClientFactory(handler));
        var schema = JsonDocument.Parse("""{ "type": "object" }""").RootElement;
        var keyless = new ResolvedModelCredential { Provider = "OpenAI", ApiKey = null, BaseUrl = "https://local-gw.local/v1" };

        var result = await client.CompleteStructuredAsync(StructuredRequest(schema, keyless), CancellationToken.None);

        result.Json.GetProperty("ok").GetBoolean().ShouldBeTrue("a keyless gateway runs the in-process structured call");
        handler.Sent.ShouldBeTrue("a keyless credential is a keyless ENDPOINT — the call goes through, never fail-closed");
        handler.AuthorizationHeader.ShouldBeNull("no key → NO Authorization header (the endpoint decides; a public API would 401)");
        handler.RequestUri.ShouldBe("https://local-gw.local/v1/chat/completions");
    }

    [Fact]
    public async Task Free_text_completion_returns_the_message_content()
    {
        const string response = """{ "model": "gpt-4o", "choices": [ { "message": { "role": "assistant", "content": "hello there" }, "finish_reason": "stop" } ], "usage": { "prompt_tokens": 3, "completion_tokens": 2 } }""";
        var client = new OpenAiClient(new StubHttpClientFactory(new CapturingHandler(response)));

        var result = await client.CompleteAsync(new LLMCompletionRequest { Model = "gpt-4o", SystemPrompt = "s", UserPrompt = "u", Credential = TestCredential }, CancellationToken.None);

        result.Text.ShouldBe("hello there");
        result.Usage.InputTokens.ShouldBe(3);
        result.Usage.OutputTokens.ShouldBe(2);
        result.Usage.FinishReason.ShouldBe("stop", "the OpenAI finish_reason is surfaced on the usage envelope");
    }

    [Fact]
    public async Task A_non_success_status_throws_with_the_body()
    {
        // A PERSISTENT 400 (both the forced-function attempt AND the prompt-only floor) surfaces as a TYPED BadRequest
        // carrying the status + the provider's error body — not an untyped exception with the status buried in prose.
        var handler = new CapturingHandler("""{ "error": { "message": "bad model" } }""", HttpStatusCode.BadRequest);
        var client = new OpenAiClient(new StubHttpClientFactory(handler));
        var schema = JsonDocument.Parse("""{ "type": "object" }""").RootElement;

        var ex = await Should.ThrowAsync<LlmApiException>(() => client.CompleteStructuredAsync(StructuredRequest(schema, TestCredential), CancellationToken.None));

        ex.Category.ShouldBe(LlmErrorCategory.BadRequest);
        ex.StatusCode.ShouldBe(400);
        ex.ProviderMessage.ShouldContain("bad model");
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
