using CodeSpace.Core.Services.Workflows.Llm;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins <see cref="LlmModelCapabilities"/> — the per-model wire-capability registry (the in-process
/// <c>get_supported_openai_params</c>/<c>drop_params</c> analogue). Three axes: does a model accept sampling params;
/// does the OpenAI wire want <c>max_completion_tokens</c> vs the classic <c>max_tokens</c>; and the required-field
/// output-cap default. All three are prefix-matched (incl. a provider-qualified <c>anthropic.</c> id), env-extensible
/// (Rule 8), default-allow for unknown ids, and the env-var literals are hard-pinned so a rename is compile-visible.
/// </summary>
[Trait("Category", "Unit")]
public class LlmModelCapabilitiesTests
{
    // ── sampling acceptance ───────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    // Anthropic reasoning tier — temperature/top_p/top_k removed (400 if sent) → does NOT accept sampling.
    [InlineData("claude-opus-4-8", false)]
    [InlineData("claude-opus-4-7", false)]
    [InlineData("claude-sonnet-5", false)]
    [InlineData("claude-fable-5", false)]
    [InlineData("claude-mythos-5", false)]
    [InlineData("anthropic.claude-opus-4-8", false)]   // Bedrock-qualified id — matched after the provider dot
    // OpenAI reasoning series — only the default temperature is allowed.
    [InlineData("o1", false)]
    [InlineData("o1-mini", false)]
    [InlineData("o3-mini", false)]
    [InlineData("o4-mini", false)]
    [InlineData("gpt-5.4", false)]
    // Non-reasoning models — accept an explicit temperature (determinism honoured).
    [InlineData("claude-sonnet-4-5", true)]
    [InlineData("claude-opus-4-5", true)]
    [InlineData("gpt-4o", true)]
    [InlineData("metis-coder-max", true)]              // a custom-gateway model name — unknown → accepts
    [InlineData("orca-13b", true)]                     // the 'o1' prefix must NOT false-match an unrelated 'o…' id
    public void AcceptsSampling_classifies_the_model_by_family(string model, bool accepts) =>
        LlmModelCapabilities.AcceptsSampling(model, rawNoSamplingOverride: null).ShouldBe(accepts);

    // ── output-cap param name (max_tokens vs max_completion_tokens) ───────────────────────────────────────────────

    [Theory]
    // OpenAI reasoning series REQUIRE max_completion_tokens (they 400 on the deprecated max_tokens).
    [InlineData("o1", true)]
    [InlineData("o3-mini", true)]
    [InlineData("o4-mini", true)]
    [InlineData("gpt-5.4", true)]
    // Everything else keeps the classic, universally-understood max_tokens.
    [InlineData("gpt-4o", false)]
    [InlineData("gpt-4o-mini", false)]
    [InlineData("metis-coder-max", false)]
    // Anthropic ids never use max_completion_tokens (they never reach the OpenAI wire, but the classifier is unambiguous).
    [InlineData("claude-opus-4-8", false)]
    public void UsesMaxCompletionTokens_flags_only_the_openai_reasoning_series(string model, bool uses) =>
        LlmModelCapabilities.UsesMaxCompletionTokens(model, rawOverride: null).ShouldBe(uses);

    [Fact]
    public void A_blank_model_accepts_sampling_and_uses_classic_max_tokens()
    {
        LlmModelCapabilities.AcceptsSampling(null, null).ShouldBeTrue();
        LlmModelCapabilities.AcceptsSampling("   ", null).ShouldBeTrue();
        LlmModelCapabilities.UsesMaxCompletionTokens(null, null).ShouldBeFalse();
        LlmModelCapabilities.UsesMaxCompletionTokens("   ", null).ShouldBeFalse();
    }

    // ── env overrides (Rule 8) ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_env_overrides_append_operator_prefixes_without_disturbing_the_built_in_sets()
    {
        // A gateway's own reasoning model, not in the built-in families, is covered with no code change.
        LlmModelCapabilities.AcceptsSampling("my-reasoner-v2", "my-reasoner").ShouldBeFalse();
        LlmModelCapabilities.UsesMaxCompletionTokens("house-thinker-01", "house-thinker, other-brand").ShouldBeTrue();

        // The built-in sets still apply alongside the override, and an unrelated model is unaffected.
        LlmModelCapabilities.AcceptsSampling("claude-opus-4-8", "my-reasoner").ShouldBeFalse();
        LlmModelCapabilities.AcceptsSampling("gpt-4o", "my-reasoner").ShouldBeTrue();
        LlmModelCapabilities.UsesMaxCompletionTokens("gpt-4o", "house-thinker").ShouldBeFalse();
    }

    // ── required-field output-cap default ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("16000", 16000)]   // a positive env override wins
    [InlineData(null, 8192)]       // unset → the built-in generous, non-streaming-safe default
    [InlineData("", 8192)]
    [InlineData("0", 8192)]        // non-positive → default
    [InlineData("nonsense", 8192)] // unparseable → default
    public void ResolvePositive_picks_the_env_override_or_the_default(string? raw, int expected) =>
        LlmModelCapabilities.ResolvePositive(raw, LlmModelCapabilities.DefaultMaxOutputTokensFallback).ShouldBe(expected);

    // ── per-model output ceiling ──────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("claude-opus-4-8", 128_000)]
    [InlineData("claude-sonnet-5", 128_000)]
    [InlineData("claude-fable-5", 128_000)]
    [InlineData("anthropic.claude-opus-4-8", 128_000)]   // Bedrock-qualified id — matched after the provider dot
    [InlineData("claude-haiku-4-5", 64_000)]
    public void MaxOutputCeiling_returns_the_true_max_for_known_models(string model, int ceiling) =>
        LlmModelCapabilities.MaxOutputCeiling(model, rawOverride: null).ShouldBe(ceiling);

    [Theory]
    [InlineData("gpt-4o")]          // OpenAI models aren't in the ceiling map (the OpenAI wire omits / self-limits) → unknown
    [InlineData("metis-coder-max")] // a custom-gateway model → unknown
    [InlineData(null)]
    public void MaxOutputCeiling_is_null_for_an_unknown_model(string? model) =>
        LlmModelCapabilities.MaxOutputCeiling(model, rawOverride: null).ShouldBeNull();

    [Fact]
    public void The_output_ceiling_env_override_pins_a_gateway_models_max()
    {
        LlmModelCapabilities.MaxOutputCeiling("house-lm-7b", "house-lm=8000, other=4096").ShouldBe(8000);
        LlmModelCapabilities.MaxOutputCeiling("gpt-4o", "house-lm=8000").ShouldBeNull("an override doesn't invent a ceiling for an unrelated model");
        LlmModelCapabilities.MaxOutputCeiling("claude-opus-4-8", "claude-opus-4-8=200000").ShouldBe(200_000, "an operator override wins over the built-in");
    }

    // ── output budget + streaming decision ────────────────────────────────────────────────────────────────────────

    [Theory]
    // A small explicit cap: sent verbatim, NON-streaming (byte-identical to the pre-streaming path).
    [InlineData("claude-opus-4-8", 1024, true, 1024, false)]
    [InlineData("gpt-4o", 4096, false, 4096, false)]
    // A large explicit cap: streams so a slow generation can't idle-timeout.
    [InlineData("claude-opus-4-8", 32000, true, 32000, true)]
    [InlineData("gpt-4o", 60000, false, 60000, true)]
    // An over-large ask on Anthropic: CLAMPED to the model ceiling (never 400) — and streams.
    [InlineData("claude-opus-4-8", 500000, true, 128000, true)]
    [InlineData("claude-haiku-4-5", 100000, true, 64000, true)]
    // A null cap ("let the model decide"): conservative + non-streaming. Anthropic sends the default; OpenAI omits.
    [InlineData("claude-opus-4-8", null, true, 8192, false)]
    [InlineData("gpt-4o", null, false, null, false)]
    public void ResolveOutputBudget_clamps_and_decides_streaming(string model, int? requested, bool requiresField, int? expectedCap, bool expectedStream)
    {
        var (cap, stream) = LlmModelCapabilities.ResolveOutputBudget(model, requested, requiresField);

        cap.ShouldBe(expectedCap);
        stream.ShouldBe(expectedStream);
    }

    [Fact]
    public void The_streaming_threshold_default_and_resolver_are_pinned()
    {
        LlmModelCapabilities.ResolvePositive(null, LlmModelCapabilities.StreamingThresholdDefault).ShouldBe(21000);
        LlmModelCapabilities.ResolvePositive("8000", LlmModelCapabilities.StreamingThresholdDefault).ShouldBe(8000);   // env-tunable: a lower threshold streams a mid-size cap
    }

    [Fact]
    public void The_env_var_constant_names_are_pinned()
    {
        // Renaming any of these silently breaks every operator who tuned a gateway model / a slow deployment via env.
        LlmModelCapabilities.NoSamplingModelsEnvVar.ShouldBe("CODESPACE_LLM_NO_SAMPLING_MODELS");
        LlmModelCapabilities.MaxCompletionTokensModelsEnvVar.ShouldBe("CODESPACE_LLM_MAX_COMPLETION_TOKENS_MODELS");
        LlmModelCapabilities.DefaultMaxOutputTokensEnvVar.ShouldBe("CODESPACE_LLM_DEFAULT_MAX_OUTPUT_TOKENS");
        LlmModelCapabilities.StreamingThresholdEnvVar.ShouldBe("CODESPACE_LLM_STREAMING_THRESHOLD_TOKENS");
        LlmModelCapabilities.OutputCeilingsEnvVar.ShouldBe("CODESPACE_LLM_OUTPUT_CEILINGS");
    }
}
