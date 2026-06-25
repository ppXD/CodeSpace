using System.Net;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Llm.Anthropic;
using CodeSpace.Messages.Agents;
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
        public string? ApiKeyHeader;
        public string? RequestUri;
        private readonly string _responseJson;
        public CapturingHandler(string responseJson) { _responseJson = responseJson; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ApiKeyHeader = request.Headers.TryGetValues("x-api-key", out var v) ? v.FirstOrDefault() : null;
            RequestUri = request.RequestUri?.ToString();
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

    [Fact]
    public async Task Structured_completion_forces_a_tool_and_extracts_its_input()
    {
        const string response = """
            {
              "model": "claude-sonnet-4-5",
              "content": [
                { "type": "tool_use", "id": "toolu_1", "name": "respond", "input": { "subtasks": ["x", "y"] } }
              ],
              "usage": { "input_tokens": 12, "output_tokens": 7 },
              "stop_reason": "tool_use"
            }
            """;
        var handler = new CapturingHandler(response);
        var client = new AnthropicClient(new StubHttpClientFactory(handler));

        var schema = JsonDocument.Parse("""{ "type": "object", "properties": { "subtasks": { "type": "array" } } }""").RootElement;

        var result = await client.CompleteStructuredAsync(new StructuredLLMCompletionRequest
        {
            Model = "claude-sonnet-4-5", SystemPrompt = "sys", UserPrompt = "decompose this", JsonSchema = schema,
            Credential = TestCredential,
        }, CancellationToken.None);

        var (json, model, inTok, outTok) = (result.Json, result.Model, result.Usage.InputTokens, result.Usage.OutputTokens);

        // Response extraction: tool_use.input became the JSON.
        json.GetProperty("subtasks")[0].GetString().ShouldBe("x");
        json.GetProperty("subtasks").GetArrayLength().ShouldBe(2);
        model.ShouldBe("claude-sonnet-4-5");
        inTok.ShouldBe(12);
        outTok.ShouldBe(7);
        result.Usage.FinishReason.ShouldBe("tool_use", "the Anthropic stop_reason is surfaced on the usage envelope");

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
    public async Task Structured_completion_throws_when_neither_attempt_yields_json()
    {
        // Model answered with plain prose (no tool, no JSON) on BOTH the forced-tool attempt and the prompt-only
        // floor — surface it with a content preview, don't return garbage.
        const string response = """{ "model": "m", "content": [ { "type": "text", "text": "I cannot." } ], "usage": { "input_tokens": 1, "output_tokens": 1 } }""";
        var client = new AnthropicClient(new StubHttpClientFactory(new CapturingHandler(response)));
        var schema = JsonDocument.Parse("""{ "type": "object" }""").RootElement;

        // A model that yields no structured output on BOTH attempts is a typed Malformed capability fault (so the
        // supervisor decider can fail it closed to a clean stop rather than crash the run), not a bare InvalidOperationException.
        var ex = await Should.ThrowAsync<LlmApiException>(() => client.CompleteStructuredAsync(new StructuredLLMCompletionRequest
        {
            Model = "m", SystemPrompt = "", UserPrompt = "x", JsonSchema = schema, Credential = TestCredential,
        }, CancellationToken.None));

        ex.Category.ShouldBe(LlmErrorCategory.Malformed);
        ex.Message.ShouldContain("did not produce structured output");
        ex.Message.ShouldContain("I cannot.", customMessage: "the content preview names what the model actually returned");
    }

    [Fact]
    public async Task Degrades_to_a_prompt_only_request_when_the_forced_tool_returns_empty_content()
    {
        // The real-gateway case the content-preview diagnosis surfaced: forcing the tool yields an EMPTY reply. The
        // client must degrade to a prompt-only request (no tools) and recover the JSON the model then returns as text.
        var handler = new SequencedHandler(
            (HttpStatusCode.OK, """{ "model": "m", "content": [] }"""),
            (HttpStatusCode.OK, """{ "model": "m", "content": [ { "type": "text", "text": "{\"kind\":\"plan\"}" } ] }"""));
        var client = new AnthropicClient(new StubHttpClientFactory(handler));
        var schema = JsonDocument.Parse("""{ "type": "object" }""").RootElement;

        var result = await client.CompleteStructuredAsync(new StructuredLLMCompletionRequest
        {
            Model = "m", SystemPrompt = "s", UserPrompt = "u", JsonSchema = schema, Credential = TestCredential,
        }, CancellationToken.None);

        result.Json.GetProperty("kind").GetString().ShouldBe("plan");
        handler.RequestBodies.Count.ShouldBe(2, "attempt 1 (forced tool) then attempt 2 (prompt-only) both hit the endpoint");
        JsonDocument.Parse(handler.RequestBodies[0]).RootElement.TryGetProperty("tool_choice", out _).ShouldBeTrue("attempt 1 forces the tool");
        JsonDocument.Parse(handler.RequestBodies[1]).RootElement.TryGetProperty("tools", out _).ShouldBeFalse("attempt 2 sends NO tools — the safest request");
    }

    [Fact]
    public async Task Degrades_to_a_prompt_only_request_when_the_forced_tool_is_rejected_with_a_400()
    {
        // A gateway 400s the forced-tool request (the feature is unsupported), then answers the prompt-only retry in
        // plain text. The client must NOT surface the 400 — it must degrade and recover.
        var handler = new SequencedHandler(
            (HttpStatusCode.BadRequest, """{ "error": { "message": "tool_choice not supported" } }"""),
            (HttpStatusCode.OK, """{ "model": "m", "content": [ { "type": "text", "text": "{\"kind\":\"merge\"}" } ] }"""));
        var client = new AnthropicClient(new StubHttpClientFactory(handler));
        var schema = JsonDocument.Parse("""{ "type": "object" }""").RootElement;

        var result = await client.CompleteStructuredAsync(new StructuredLLMCompletionRequest
        {
            Model = "m", SystemPrompt = "s", UserPrompt = "u", JsonSchema = schema, Credential = TestCredential,
        }, CancellationToken.None);

        result.Json.GetProperty("kind").GetString().ShouldBe("merge");
        handler.RequestBodies.Count.ShouldBe(2, "attempt 1 400'd, so the prompt-only floor ran");
    }

    [Fact]
    public async Task Usage_totals_the_billed_forced_attempt_plus_the_floor_when_it_degrades()
    {
        // The forced-tool attempt is a 200 with usage (BILLED) but yields no tool_use → degrades to the floor, also
        // billed. The returned usage must TOTAL both POSTs, not just the floor — else a degraded structured call
        // under-reports what the provider actually charged.
        var handler = new SequencedHandler(
            (HttpStatusCode.OK, """{ "model": "m", "content": [], "usage": { "input_tokens": 12, "output_tokens": 7 }, "stop_reason": "tool_use" }"""),
            (HttpStatusCode.OK, """{ "model": "m", "content": [ { "type": "text", "text": "{\"kind\":\"plan\"}" } ], "usage": { "input_tokens": 8, "output_tokens": 5 }, "stop_reason": "end_turn" }"""));
        var client = new AnthropicClient(new StubHttpClientFactory(handler));
        var schema = JsonDocument.Parse("""{ "type": "object" }""").RootElement;

        var result = await client.CompleteStructuredAsync(new StructuredLLMCompletionRequest
        {
            Model = "m", SystemPrompt = "s", UserPrompt = "u", JsonSchema = schema, Credential = TestCredential,
        }, CancellationToken.None);

        result.Json.GetProperty("kind").GetString().ShouldBe("plan");
        result.Usage.InputTokens.ShouldBe(20, "the billed forced attempt (12) + the floor (8) — never just the floor");
        result.Usage.OutputTokens.ShouldBe(12, "7 + 5");
        result.Usage.FinishReason.ShouldBe("end_turn", "the LATER (accepted floor) sub-call's reason, not the degraded forced attempt's");
    }

    [Fact]
    public async Task A_400_rejected_forced_attempt_is_not_billed_so_only_the_floor_counts()
    {
        // The forced attempt 400s (rejected pre-generation → NOT billed). Only the floor's usage counts — the reject
        // must not be summed in (it never cost a token).
        var handler = new SequencedHandler(
            (HttpStatusCode.BadRequest, """{ "error": { "message": "tool_choice not supported" } }"""),
            (HttpStatusCode.OK, """{ "model": "m", "content": [ { "type": "text", "text": "{\"kind\":\"merge\"}" } ], "usage": { "input_tokens": 9, "output_tokens": 4 }, "stop_reason": "end_turn" }"""));
        var client = new AnthropicClient(new StubHttpClientFactory(handler));
        var schema = JsonDocument.Parse("""{ "type": "object" }""").RootElement;

        var result = await client.CompleteStructuredAsync(new StructuredLLMCompletionRequest
        {
            Model = "m", SystemPrompt = "s", UserPrompt = "u", JsonSchema = schema, Credential = TestCredential,
        }, CancellationToken.None);

        result.Json.GetProperty("kind").GetString().ShouldBe("merge");
        result.Usage.InputTokens.ShouldBe(9, "the 400 was rejected pre-generation (not billed) — only the floor's 9 counts");
        result.Usage.OutputTokens.ShouldBe(4);
    }

    [Fact]
    public async Task Usage_accumulates_across_a_schema_invalid_attempt_and_the_re_ask()
    {
        // Attempt 1's forced tool returns {} — valid JSON but schema-INVALID (missing the required "kind"); billed
        // (10/6). The client re-asks, billed again (11/7). The returned usage must TOTAL both — a re-asked structured
        // call bills twice, and reporting only the second under-counts the spend.
        var schema = JsonDocument.Parse("""{ "type": "object", "required": ["kind"], "properties": { "kind": { "type": "string" } } }""").RootElement;
        var handler = new SequencedHandler(
            (HttpStatusCode.OK, """{ "model": "m", "content": [ { "type": "tool_use", "name": "respond", "input": {} } ], "usage": { "input_tokens": 10, "output_tokens": 6 }, "stop_reason": "tool_use" }"""),
            (HttpStatusCode.OK, """{ "model": "m", "content": [ { "type": "tool_use", "name": "respond", "input": { "kind": "plan" } } ], "usage": { "input_tokens": 11, "output_tokens": 7 }, "stop_reason": "tool_use" }"""));
        var client = new AnthropicClient(new StubHttpClientFactory(handler));

        var result = await client.CompleteStructuredAsync(new StructuredLLMCompletionRequest
        {
            Model = "m", SystemPrompt = "s", UserPrompt = "u", JsonSchema = schema, Credential = TestCredential,
        }, CancellationToken.None);

        result.Json.GetProperty("kind").GetString().ShouldBe("plan");
        result.Usage.InputTokens.ShouldBe(21, "attempt 1 (10) + the re-ask (11) — a re-asked structured call bills twice");
        result.Usage.OutputTokens.ShouldBe(13, "6 + 7");
    }

    [Fact]
    public async Task Structured_completion_falls_back_to_json_in_text_content_when_the_model_skips_the_tool()
    {
        // The real-endpoint case: the model IGNORED the forced tool and put the JSON in a text block. Recover it.
        var inner = "Here is the result: {\"subtasks\":[\"a\",\"b\"]}";
        var response = JsonSerializer.Serialize(new { model = "m", content = new[] { new { type = "text", text = inner } } });
        var client = new AnthropicClient(new StubHttpClientFactory(new CapturingHandler(response)));
        var schema = JsonDocument.Parse("""{ "type": "object" }""").RootElement;

        var result = await client.CompleteStructuredAsync(new StructuredLLMCompletionRequest
        {
            Model = "m", SystemPrompt = "s", UserPrompt = "u", JsonSchema = schema, Credential = TestCredential,
        }, CancellationToken.None);

        result.Json.GetProperty("subtasks")[0].GetString().ShouldBe("a");
        result.Json.GetProperty("subtasks").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task The_request_credential_key_and_base_url_authenticate_the_call()
    {
        const string response = """{ "model": "m", "content": [ { "type": "tool_use", "name": "respond", "input": {} } ] }""";
        var handler = new CapturingHandler(response);
        var client = new AnthropicClient(new StubHttpClientFactory(handler));
        var schema = JsonDocument.Parse("""{ "type": "object" }""").RootElement;

        // Even with a poison env key set, S6b reads ONLY the credential — proving the env is no longer a backstop.
        await WithEnvKeyAsync("poison-env-key", async () =>
        {
            await client.CompleteStructuredAsync(new StructuredLLMCompletionRequest
            {
                Model = "m", SystemPrompt = "s", UserPrompt = "u", JsonSchema = schema,
                Credential = new ResolvedModelCredential { Provider = "Anthropic", ApiKey = "team-key", BaseUrl = "https://team.gateway" },
            }, CancellationToken.None);
        });

        handler.ApiKeyHeader.ShouldBe("team-key", "the resolved credential's key authenticates the call — the env is never read");
        handler.RequestUri!.ShouldStartWith("https://team.gateway");   // the credential's base URL drives the request, not the env
    }

    [Fact]
    public async Task Without_a_credential_the_call_fails_closed_even_when_the_env_key_is_set()
    {
        const string response = """{ "model": "m", "content": [ { "type": "tool_use", "name": "respond", "input": {} } ] }""";
        var handler = new CapturingHandler(response);
        var client = new AnthropicClient(new StubHttpClientFactory(handler));
        var schema = JsonDocument.Parse("""{ "type": "object" }""").RootElement;

        // S6b: no env-key backstop. A caller that passes no credential fails closed — it must NOT borrow the env key.
        await WithEnvKeyAsync("test-key", async () =>
        {
            var ex = await Should.ThrowAsync<InvalidOperationException>(() => client.CompleteStructuredAsync(
                new StructuredLLMCompletionRequest { Model = "m", SystemPrompt = "s", UserPrompt = "u", JsonSchema = schema }, CancellationToken.None));
            ex.Message.ShouldContain("credential not configured", Case.Insensitive, "a NULL credential fails closed (the KEY is optional, but the credential is required — no env-key borrow)");
        });
    }

    [Fact]
    public async Task A_keyless_credential_sends_no_x_api_key_header_so_a_local_gateway_runs()
    {
        // A keyless Anthropic-compatible gateway: a credential with a base URL but no key sends NO x-api-key header.
        const string response = """
            { "model": "m", "content": [ { "type": "tool_use", "id": "t1", "name": "respond", "input": { "ok": true } } ], "stop_reason": "tool_use" }
            """;
        var handler = new CapturingHandler(response);
        var client = new AnthropicClient(new StubHttpClientFactory(handler));
        var schema = JsonDocument.Parse("""{ "type": "object" }""").RootElement;
        var keyless = new ResolvedModelCredential { Provider = "Anthropic", ApiKey = null, BaseUrl = "https://local-gw.local" };

        var result = await client.CompleteStructuredAsync(
            new StructuredLLMCompletionRequest { Model = "m", SystemPrompt = "s", UserPrompt = "u", JsonSchema = schema, Credential = keyless }, CancellationToken.None);

        result.Json.GetProperty("ok").GetBoolean().ShouldBeTrue("a keyless gateway runs the in-process structured call");
        handler.ApiKeyHeader.ShouldBeNull("no key → NO x-api-key header (the endpoint decides)");
        handler.RequestUri.ShouldBe("https://local-gw.local/v1/messages");

        handler.ApiKeyHeader.ShouldBeNull("the call must fail closed before any request is sent — never reaching the env key");
    }

    private static readonly ResolvedModelCredential TestCredential = new() { Provider = "Anthropic", ApiKey = "test-key" };

    /// <summary>Sets the API-key env var (the AGENT plane / cassette gate still read it) to prove the in-process call path IGNORES it, then restores it.</summary>
    private static async Task WithEnvKeyAsync(string value, Func<Task> action)
    {
        var oldKey = Environment.GetEnvironmentVariable(AnthropicClient.ApiKeyEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(AnthropicClient.ApiKeyEnvVar, value);
            await action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(AnthropicClient.ApiKeyEnvVar, oldKey);
        }
    }
}
