using CodeSpace.Core.Services.Workflows.Llm;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// The HONEST fake at the <see cref="ILLMClient"/> (plain-text) boundary — the SYNTHESIZER half of the
/// plan-map-synth flow. The synth <c>llm.complete</c> node resolves THIS client (registered under the distinct
/// provider tag <see cref="ProviderTag"/>, so it sits alongside the real Anthropic client + the planner fake
/// with no duplicate-provider collision) and gets back a DETERMINISTIC reduce of its <c>userPrompt</c>.
///
/// <para>Crucially it is NOT a passthrough: it prepends <see cref="Prefix"/> and folds the LENGTH of the
/// userPrompt into the output — a stable transform the test asserts to prove the synth node actually READ the
/// per-branch results array (a raw-array re-bind, the old <c>builtin.terminal</c> behaviour, could never produce
/// this). It implements the same <see cref="ILLMClient"/> the production <c>AnthropicClient</c> does, so the
/// node routes through the real text-completion path (<c>CompleteAsync</c> → the <c>text</c> output mapping) —
/// only the network call to a real model is replaced.</para>
/// </summary>
public sealed class DeterministicSynthLlmClient : ILLMClient
{
    /// <summary>The provider tag the synth node selects (config <c>provider</c>). Distinct from "Anthropic" and the planner fake's tag so the registry holds all three without a duplicate-provider collision.</summary>
    public const string ProviderTag = "TestSynth";

    /// <summary>The fixed prefix the reduce stamps onto its output — the recognisable marker the test asserts (a raw-array re-bind could never produce it).</summary>
    public const string Prefix = "SYNTH[";

    public string Provider => ProviderTag;

    /// <summary>The deterministic reduce the fake produces for a given userPrompt — a stable transform (prefix + length) the test recomputes to prove the synth read the WHOLE results array, not a passthrough.</summary>
    public static string ExpectedReduceFor(string userPrompt) => $"{Prefix}{userPrompt.Length}]: {userPrompt}";

    public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new LLMCompletion { Text = ExpectedReduceFor(request.UserPrompt), Model = request.Model, InputTokens = 17, OutputTokens = 19 });
}
