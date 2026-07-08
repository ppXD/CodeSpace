using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Llm;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// The real-model proof that the GENERIC "let the model decide" path drives a LIVE model end to end: a structured
/// completion with BOTH <c>Temperature = null</c> AND <c>MaxOutputTokens = null</c> — so the transport sends NO
/// <c>temperature</c> and NO output cap on the wire (the OpenAI wire omits it entirely; the Anthropic wire sends only its
/// required default) — still returns schema-conformant JSON from the real gateway. This is the live counterpart of the
/// deterministic <c>LlmSamplingWireTests</c> (which pins the omission bytes) + <c>LlmModelCapabilitiesTests</c> (which pins
/// the reasoning-tier drop + the max_tokens⇄max_completion_tokens rename): those prove the BYTES; this proves a real model
/// on the configured endpoint actually completes with the fully-generic, un-pinned request — the exact path a reasoning-tier
/// brain (Opus 4.8 / Sonnet 5 / an o-series model) forces, where a pinned temperature or a bare max_tokens would 400.
///
/// <para>A <c>[Theory]</c> over the Anthropic + Custom (OpenAI-compatible) wires from the ONE configured gateway.
/// HONESTLY GATED on the <c>CODESPACE_LLM_*</c> secrets (absent ⇒ skip, so CI/forks stay green at zero cost); the
/// blessed Anthropic wire GATES (a non-conformant reply fails the job) while Custom is INFORMATIONAL, and a gateway
/// timeout is non-gating infra — all via <see cref="RealModelGate.AssessLiveAsync(string, System.Func{System.Threading.Tasks.Task{System.ValueTuple{bool, string}}}, bool)"/>.
/// Constructs the real client directly (no Postgres), so each run is a fresh live measurement.</para>
/// </summary>
[Trait("Category", "RealModel")]
public sealed class RealModelSamplingOmissionE2ETests
{
    private static readonly JsonElement AnswerSchema = JsonDocument.Parse(
        """{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"],"additionalProperties":false}""").RootElement;

    [Theory]
    [InlineData("Anthropic")]
    [InlineData("Custom")]
    public async Task A_temperature_omitted_structured_call_drives_the_live_model(string provider)
    {
        var baseUrl = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        if (baseUrl is null || apiKey is null || model is null) return;   // secrets absent → honest skip (green CI/fork)

        await RealModelGate.AssessLiveAsync(provider, async () =>
        {
            var credential = RealModelLiveWire.Credential(provider, baseUrl, apiKey);
            var client = (IStructuredLLMClient)RealModelLiveWire.Registry().Resolve(provider);

            var request = new StructuredLLMCompletionRequest
            {
                Model = model,
                Credential = credential,
                SystemPrompt = "You output only the requested JSON object, nothing else.",
                UserPrompt = "Reply with a JSON object whose \"answer\" field is exactly the word ok.",
                Temperature = null,     // the generic path: NO temperature on the wire — the model/provider decides
                MaxOutputTokens = null, // and NO output cap — OpenAI omits it, Anthropic sends only its required default (the schema bounds the reply)
                JsonSchema = AnswerSchema,
            };

            var result = await client.CompleteStructuredAsync(request, CancellationToken.None);

            var conformant = result.Json.ValueKind == JsonValueKind.Object
                && result.Json.TryGetProperty("answer", out var answer)
                && answer.ValueKind == JsonValueKind.String;

            return (conformant, $"temperature-omitted structured call on '{model}' via {provider} → {(conformant ? "schema-conformant JSON (answer present)" : $"NON-conformant reply: {result.Json.GetRawText()}")}");
        });
    }

    /// <summary>
    /// The live proof that the STREAMING path drives a real model end to end: a large output cap (above the streaming
    /// threshold) makes the transport stream the completion, and the SSE events fold back into a non-empty reply. A short
    /// prompt keeps it fast + cheap while still forcing the streamed path. REPORT-ONLY (gating:false) on both wires — an
    /// arbitrary gateway model's true ceiling is unknown, so a rare max_tokens-too-large 400 must never red the lane; the
    /// SSE-accumulation correctness is GATED by the deterministic <c>LlmSamplingWireTests</c>, and this is the live smoke.
    /// </summary>
    [Theory]
    [InlineData("Anthropic")]
    [InlineData("Custom")]
    public async Task A_large_cap_streams_a_live_text_completion(string provider)
    {
        var baseUrl = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        if (baseUrl is null || apiKey is null || model is null) return;   // secrets absent → honest skip (green CI/fork)

        await RealModelGate.AssessLiveAsync(provider, async () =>
        {
            var credential = RealModelLiveWire.Credential(provider, baseUrl, apiKey);
            var client = RealModelLiveWire.Registry().Resolve(provider);

            var request = new LLMCompletionRequest
            {
                Model = model,
                Credential = credential,
                SystemPrompt = "Reply in one short sentence.",
                UserPrompt = "Say hello.",
                MaxOutputTokens = 22_000,   // > the 21K streaming threshold → the transport STREAMS; the short prompt keeps it fast + cheap
            };

            var result = await client.CompleteAsync(request, CancellationToken.None);
            var ok = !string.IsNullOrWhiteSpace(result.Text);

            return (ok, $"large-cap (22000 → streamed) text completion on '{model}' via {provider} → {(ok ? $"streamed {result.Text.Length} chars, finish={result.Usage.FinishReason}" : "EMPTY reply")}");
        }, gating: false);
    }

    /// <summary>
    /// The live proof that <c>reasoning_effort</c> rides the generic path without breaking a real model: a Custom
    /// (OpenAI-compatible) completion carrying <c>ReasoningEffort = "low"</c> still returns a non-empty reply. If the
    /// configured gateway model is an OpenAI reasoning model, the param is honored; if not,
    /// <see cref="LlmModelCapabilities.SupportsReasoningEffort(string?)"/> DROPS it before the wire so it never 400s —
    /// either way a successful completion proves the mapping is non-breaking. REPORT-ONLY (gating:false): whether the ONE
    /// configured model is a reasoning model is unknown, so the outcome must never red the lane; the drop/keep BYTES are
    /// pinned deterministically by <c>LlmSamplingWireTests</c>. Custom wire only — <c>reasoning_effort</c> is an
    /// OpenAI-wire concept the Anthropic client never maps.
    /// </summary>
    [Fact]
    public async Task Reasoning_effort_rides_the_generic_path_on_the_live_custom_wire()
    {
        var baseUrl = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);

        if (baseUrl is null || apiKey is null || model is null) return;   // secrets absent → honest skip (green CI/fork)

        await RealModelGate.AssessLiveAsync("Custom", async () =>
        {
            var credential = RealModelLiveWire.Credential("Custom", baseUrl, apiKey);
            var client = RealModelLiveWire.Registry().Resolve("Custom");

            var request = new LLMCompletionRequest
            {
                Model = model,
                Credential = credential,
                SystemPrompt = "Reply in one short sentence.",
                UserPrompt = "Say hello.",
                ReasoningEffort = "low",   // rides iff the model is a reasoning model; otherwise dropped before the wire — non-breaking either way
            };

            var result = await client.CompleteAsync(request, CancellationToken.None);
            var ok = !string.IsNullOrWhiteSpace(result.Text);
            var honored = LlmModelCapabilities.SupportsReasoningEffort(model);

            return (ok, $"reasoning_effort=low on '{model}' via Custom → {(ok ? $"completed ({result.Text.Length} chars); param {(honored ? "sent (reasoning model)" : "dropped (non-reasoning model)")}" : "EMPTY reply")}");
        }, gating: false);
    }
}
