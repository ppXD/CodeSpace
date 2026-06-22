using System.Net;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Llm.Anthropic;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins PR-3's structured-output correctness: the recovered object is VALIDATED against the schema, an invalid first
/// response triggers a bounded RE-ASK (naming the violations), and a persistently-invalid output is a typed Malformed
/// fault — not a silently-returned garbage object. Plus the hardened balanced-brace JSON extractor.
/// </summary>
[Trait("Category", "Unit")]
public class StructuredOutputValidationTests
{
    private static readonly JsonElement KindSchema =
        JsonDocument.Parse("""{"type":"object","required":["kind"],"properties":{"kind":{"type":"string","enum":["plan","merge"]}}}""").RootElement;
    private static readonly ResolvedModelCredential Cred = new() { Provider = "Anthropic", ApiKey = "k" };

    [Fact]
    public async Task An_invalid_first_response_triggers_a_re_ask_that_recovers()
    {
        // Attempt 1's forced tool returns a schema-INVALID object (no required 'kind'); validation fails → the client
        // re-asks ONCE (forced tool again) and the model now returns a valid object. Two endpoint hits, valid result.
        var handler = new SequencedHandler(
            (HttpStatusCode.OK, ToolUse("""{"wrong":"x"}""")),
            (HttpStatusCode.OK, ToolUse("""{"kind":"plan"}""")));
        var client = new AnthropicClient(Factory(handler));

        var result = await client.CompleteStructuredAsync(Req(), CancellationToken.None);

        result.Json.GetProperty("kind").GetString().ShouldBe("plan");
        handler.Count.ShouldBe(2, "one invalid attempt + one re-ask = two calls");
    }

    [Fact]
    public async Task A_persistently_invalid_output_is_a_typed_malformed_fault()
    {
        // Both the first attempt and the re-ask return schema-invalid objects → a typed Malformed fault naming the
        // violation (the engine fails it fast; it is NOT silently returned as success).
        var handler = new SequencedHandler(
            (HttpStatusCode.OK, ToolUse("""{"wrong":"x"}""")),
            (HttpStatusCode.OK, ToolUse("""{"still":"wrong"}""")));
        var client = new AnthropicClient(Factory(handler));

        var ex = await Should.ThrowAsync<LlmApiException>(() => client.CompleteStructuredAsync(Req(), CancellationToken.None));

        ex.Category.ShouldBe(LlmErrorCategory.Malformed);
        ex.Message.ShouldContain("missing required property 'kind'");
        handler.Count.ShouldBe(2, "exactly one re-ask — the validation loop is bounded, never unbounded billing");
    }

    [Fact]
    public async Task A_valid_first_response_does_not_re_ask()
    {
        var handler = new SequencedHandler((HttpStatusCode.OK, ToolUse("""{"kind":"merge"}""")));
        var client = new AnthropicClient(Factory(handler));

        var result = await client.CompleteStructuredAsync(Req(), CancellationToken.None);

        result.Json.GetProperty("kind").GetString().ShouldBe("merge");
        handler.Count.ShouldBe(1, "a conforming first response is returned immediately — no wasted re-ask");
    }

    [Theory]
    [InlineData("""prose before {"kind":"plan"} and prose after""", "plan")]            // surrounding prose
    [InlineData("""{"kind":"plan","note":"a } brace in a string"} trailing""", "plan")] // brace inside a string
    [InlineData("""{"kind":"plan"} {"kind":"merge"}""", "plan")]                          // first of two objects
    [InlineData("```json\n{\"kind\":\"plan\"}\n```", "plan")]                              // fenced
    public void The_extractor_takes_the_first_balanced_object(string content, string expectedKind)
    {
        StructuredJsonText.TryExtractObject(content)!.Value.GetProperty("kind").GetString().ShouldBe(expectedKind);
    }

    [Theory]
    [InlineData("""{"kind":"plan" """)]   // truncated — no closing brace
    [InlineData("no json here at all")]
    [InlineData("")]
    public void The_extractor_returns_null_on_no_complete_object(string content)
    {
        StructuredJsonText.TryExtractObject(content).ShouldBeNull();
    }

    // ── Harness ──────────────────────────────────────────────────────────────────────

    private static StructuredLLMCompletionRequest Req() => new()
    {
        Model = "m", SystemPrompt = "s", UserPrompt = "u", JsonSchema = KindSchema, Credential = Cred,
    };

    private static string ToolUse(string inputJson) =>
        "{\"model\":\"m\",\"content\":[{\"type\":\"tool_use\",\"name\":\"respond\",\"input\":" + inputJson + "}],\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}";

    private static IHttpClientFactory Factory(HttpMessageHandler h) => new StubFactory(h);

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _h;
        public StubFactory(HttpMessageHandler h) { _h = h; }
        public HttpClient CreateClient(string name) => new(_h, disposeHandler: false);
    }

    private sealed class SequencedHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode, string)> _responses;
        public int Count { get; private set; }
        public SequencedHandler(params (HttpStatusCode, string)[] responses) { _responses = new(responses); }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            var (status, body) = _responses.Count > 0 ? _responses.Dequeue() : (HttpStatusCode.OK, "{}");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
