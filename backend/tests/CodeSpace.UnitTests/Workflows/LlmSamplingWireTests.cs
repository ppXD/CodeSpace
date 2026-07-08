using System.Net;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Llm.Anthropic;
using CodeSpace.Core.Services.Workflows.Llm.OpenAi;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the sampling-knob wire mapping (PR-4): the shared LlmSamplingOptions is emitted onto each provider's request
/// body using ITS supported params (Anthropic: top_p + stop_sequences, NO penalties; OpenAI: all four), and OMITTED
/// entirely when null so the prior request shape is byte-identical and an unsupported gateway is never sent a key.
///
/// <para>Also pins <c>temperature</c>'s wire contract: nullable ⇒ omitted (the "let the model decide" default), and a
/// PINNED temperature (plus top_p / penalties) is DROPPED by the transport for a reasoning-tier model that would 400 on
/// it (<see cref="LlmModelCapabilities"/>) while a stop-sequence survives — so a caller that hardcodes <c>0.2</c> never
/// breaks against Opus 4.8 / Sonnet 5 / an o-series model, and a non-reasoning model keeps its pinned determinism.</para>
///
/// <para>And the OUTPUT-CAP contract: nullable ⇒ OpenAI omits it (model runs to its context limit) while Anthropic — which
/// REQUIRES <c>max_tokens</c> — sends the generous default; a set cap rides as <c>max_completion_tokens</c> for an OpenAI
/// reasoning model (which 400s on the deprecated <c>max_tokens</c>) and as classic <c>max_tokens</c> otherwise.</para>
/// </summary>
[Trait("Category", "Unit")]
public class LlmSamplingWireTests
{
    [Fact]
    public async Task Anthropic_emits_top_p_and_stop_sequences_but_never_penalties()
    {
        var handler = new CapturingHandler("""{"model":"m","content":[{"type":"text","text":"hi"}]}""");
        var client = new AnthropicClient(Factory(handler));

        await client.CompleteAsync(new LLMCompletionRequest
        {
            Model = "m", SystemPrompt = "s", UserPrompt = "u", Credential = Cred("Anthropic"),
            Sampling = new LlmSamplingOptions { TopP = 0.9, FrequencyPenalty = 1.0, PresencePenalty = 1.0, Stop = new[] { "STOP" } },
        }, CancellationToken.None);

        var body = JsonDocument.Parse(handler.Body!).RootElement;
        body.GetProperty("top_p").GetDouble().ShouldBe(0.9);
        body.GetProperty("stop_sequences")[0].GetString().ShouldBe("STOP");
        body.TryGetProperty("frequency_penalty", out _).ShouldBeFalse("Anthropic's API has no frequency penalty — it must NOT be sent");
        body.TryGetProperty("presence_penalty", out _).ShouldBeFalse("Anthropic's API has no presence penalty");
    }

    [Fact]
    public async Task OpenAi_emits_all_four_knobs()
    {
        var handler = new CapturingHandler("""{"model":"m","choices":[{"message":{"content":"hi"}}]}""");
        var client = new OpenAiClient(Factory(handler));

        await client.CompleteAsync(new LLMCompletionRequest
        {
            Model = "m", SystemPrompt = "s", UserPrompt = "u", Credential = Cred("OpenAI"),
            Sampling = new LlmSamplingOptions { TopP = 0.8, FrequencyPenalty = 0.5, PresencePenalty = 0.3, Stop = new[] { "END", "STOP" } },
        }, CancellationToken.None);

        var body = JsonDocument.Parse(handler.Body!).RootElement;
        body.GetProperty("top_p").GetDouble().ShouldBe(0.8);
        body.GetProperty("frequency_penalty").GetDouble().ShouldBe(0.5);
        body.GetProperty("presence_penalty").GetDouble().ShouldBe(0.3);
        body.GetProperty("stop").GetArrayLength().ShouldBe(2);
    }

    [Theory]
    [InlineData("Anthropic")]
    [InlineData("OpenAI")]
    public async Task No_sampling_omits_every_knob_so_the_request_is_byte_identical_to_before(string provider)
    {
        var responseJson = provider == "Anthropic"
            ? """{"model":"m","content":[{"type":"text","text":"hi"}]}"""
            : """{"model":"m","choices":[{"message":{"content":"hi"}}]}""";
        var handler = new CapturingHandler(responseJson);
        ILLMClient client = provider == "Anthropic" ? new AnthropicClient(Factory(handler)) : new OpenAiClient(Factory(handler));

        await client.CompleteAsync(new LLMCompletionRequest { Model = "m", SystemPrompt = "s", UserPrompt = "u", Credential = Cred(provider) }, CancellationToken.None);

        var body = JsonDocument.Parse(handler.Body!).RootElement;
        foreach (var key in new[] { "top_p", "stop_sequences", "stop", "frequency_penalty", "presence_penalty" })
            body.TryGetProperty(key, out _).ShouldBeFalse($"with no Sampling, '{key}' must be omitted");
    }

    // ── temperature: nullable + reasoning-tier drop (the "let the model decide" generic default) ──────────────────

    [Theory]
    // A non-reasoning model keeps the pinned temperature; a reasoning-tier model drops it (it would 400 on the param).
    [InlineData("claude-sonnet-4-5", true)]
    [InlineData("claude-opus-4-8", false)]
    [InlineData("gpt-5.4", false)]
    public async Task Anthropic_emits_temperature_only_when_the_model_accepts_it(string model, bool present)
    {
        var handler = new CapturingHandler("""{"model":"m","content":[{"type":"text","text":"hi"}]}""");
        var client = new AnthropicClient(Factory(handler));

        await client.CompleteAsync(new LLMCompletionRequest { Model = model, SystemPrompt = "s", UserPrompt = "u", Temperature = 0.2, Credential = Cred("Anthropic") }, CancellationToken.None);

        var body = JsonDocument.Parse(handler.Body!).RootElement;
        body.TryGetProperty("temperature", out var t).ShouldBe(present, present ? "a model that accepts sampling keeps the pinned temperature" : "a reasoning-tier model must NOT be sent temperature (it 400s on it)");
        if (present) t.GetDouble().ShouldBe(0.2);
    }

    [Theory]
    [InlineData("gpt-4o", true)]
    [InlineData("o1-mini", false)]
    public async Task OpenAi_emits_temperature_only_when_the_model_accepts_it(string model, bool present)
    {
        var handler = new CapturingHandler("""{"model":"m","choices":[{"message":{"content":"hi"}}]}""");
        var client = new OpenAiClient(Factory(handler));

        await client.CompleteAsync(new LLMCompletionRequest { Model = model, SystemPrompt = "s", UserPrompt = "u", Temperature = 0.2, Credential = Cred("OpenAI") }, CancellationToken.None);

        var body = JsonDocument.Parse(handler.Body!).RootElement;
        body.TryGetProperty("temperature", out var t).ShouldBe(present);
        if (present) t.GetDouble().ShouldBe(0.2);
    }

    [Theory]
    [InlineData("Anthropic")]
    [InlineData("OpenAI")]
    public async Task A_null_temperature_is_omitted_so_the_provider_default_applies(string provider)
    {
        var responseJson = provider == "Anthropic"
            ? """{"model":"m","content":[{"type":"text","text":"hi"}]}"""
            : """{"model":"m","choices":[{"message":{"content":"hi"}}]}""";
        var handler = new CapturingHandler(responseJson);
        ILLMClient client = provider == "Anthropic" ? new AnthropicClient(Factory(handler)) : new OpenAiClient(Factory(handler));

        await client.CompleteAsync(new LLMCompletionRequest { Model = "m", SystemPrompt = "s", UserPrompt = "u", Temperature = null, Credential = Cred(provider) }, CancellationToken.None);

        JsonDocument.Parse(handler.Body!).RootElement.TryGetProperty("temperature", out _)
            .ShouldBeFalse("a null temperature is the 'let the model decide' default — nothing on the wire");
    }

    [Fact]
    public async Task A_reasoning_model_drops_a_pinned_temperature_and_top_p_but_keeps_stop_sequences()
    {
        var handler = new CapturingHandler("""{"model":"m","content":[{"type":"text","text":"hi"}]}""");
        var client = new AnthropicClient(Factory(handler));

        await client.CompleteAsync(new LLMCompletionRequest
        {
            Model = "claude-opus-4-8", SystemPrompt = "s", UserPrompt = "u", Temperature = 0.2,
            Sampling = new LlmSamplingOptions { TopP = 0.9, Stop = new[] { "END" } },
            Credential = Cred("Anthropic"),
        }, CancellationToken.None);

        var body = JsonDocument.Parse(handler.Body!).RootElement;
        body.TryGetProperty("temperature", out _).ShouldBeFalse();
        body.TryGetProperty("top_p", out _).ShouldBeFalse("top_p is a sampling param a reasoning model rejects — dropped with temperature");
        body.TryGetProperty("stop_sequences", out _).ShouldBeTrue("stop_sequences are NOT rejected by reasoning models — they survive the gate");
    }

    // ── output cap: nullable + max_tokens ⇄ max_completion_tokens reconciliation ──────────────────────────────────

    [Fact]
    public async Task OpenAi_sends_max_completion_tokens_for_a_reasoning_model_and_never_max_tokens()
    {
        var handler = new CapturingHandler("""{"model":"m","choices":[{"message":{"content":"hi"}}]}""");
        var client = new OpenAiClient(Factory(handler));

        await client.CompleteAsync(new LLMCompletionRequest { Model = "o1-mini", SystemPrompt = "s", UserPrompt = "u", MaxOutputTokens = 1024, Credential = Cred("OpenAI") }, CancellationToken.None);

        var body = JsonDocument.Parse(handler.Body!).RootElement;
        body.TryGetProperty("max_tokens", out _).ShouldBeFalse("a reasoning model 400s on the deprecated max_tokens");
        body.GetProperty("max_completion_tokens").GetInt32().ShouldBe(1024);
    }

    [Fact]
    public async Task OpenAi_keeps_classic_max_tokens_for_a_non_reasoning_model()
    {
        var handler = new CapturingHandler("""{"model":"m","choices":[{"message":{"content":"hi"}}]}""");
        var client = new OpenAiClient(Factory(handler));

        await client.CompleteAsync(new LLMCompletionRequest { Model = "gpt-4o", SystemPrompt = "s", UserPrompt = "u", MaxOutputTokens = 1024, Credential = Cred("OpenAI") }, CancellationToken.None);

        var body = JsonDocument.Parse(handler.Body!).RootElement;
        body.GetProperty("max_tokens").GetInt32().ShouldBe(1024);
        body.TryGetProperty("max_completion_tokens", out _).ShouldBeFalse("a non-reasoning / custom-gateway model keeps the universally-understood max_tokens");
    }

    [Fact]
    public async Task OpenAi_omits_the_output_cap_entirely_when_it_is_null()
    {
        var handler = new CapturingHandler("""{"model":"m","choices":[{"message":{"content":"hi"}}]}""");
        var client = new OpenAiClient(Factory(handler));

        await client.CompleteAsync(new LLMCompletionRequest { Model = "gpt-4o", SystemPrompt = "s", UserPrompt = "u", MaxOutputTokens = null, Credential = Cred("OpenAI") }, CancellationToken.None);

        var body = JsonDocument.Parse(handler.Body!).RootElement;
        body.TryGetProperty("max_tokens", out _).ShouldBeFalse("null cap ⇒ let the model run to its context limit — nothing on the wire");
        body.TryGetProperty("max_completion_tokens", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(4096, 4096)]   // an explicit cap rides verbatim
    [InlineData(null, 8192)]   // null ⇒ Anthropic REQUIRES the field → the generous non-streaming-safe default
    public async Task Anthropic_always_sends_max_tokens_resolving_null_to_the_required_default(int? requested, int onWire)
    {
        var handler = new CapturingHandler("""{"model":"m","content":[{"type":"text","text":"hi"}]}""");
        var client = new AnthropicClient(Factory(handler));

        await client.CompleteAsync(new LLMCompletionRequest { Model = "claude-sonnet-4-5", SystemPrompt = "s", UserPrompt = "u", MaxOutputTokens = requested, Credential = Cred("Anthropic") }, CancellationToken.None);

        JsonDocument.Parse(handler.Body!).RootElement.GetProperty("max_tokens").GetInt32().ShouldBe(onWire);
    }

    // ── Harness ──────────────────────────────────────────────────────────────────────

    private static ResolvedModelCredential Cred(string provider) => new() { Provider = provider, ApiKey = "k" };

    private static IHttpClientFactory Factory(HttpMessageHandler h) => new StubFactory(h);

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _h;
        public StubFactory(HttpMessageHandler h) { _h = h; }
        public HttpClient CreateClient(string name) => new(_h, disposeHandler: false);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Body;
        private readonly string _response;
        public CapturingHandler(string response) { _response = response; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_response, Encoding.UTF8, "application/json") };
        }
    }
}
