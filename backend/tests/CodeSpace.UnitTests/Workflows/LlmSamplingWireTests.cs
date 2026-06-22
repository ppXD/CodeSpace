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
