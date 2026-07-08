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

    [Fact]
    public void The_env_var_constant_names_are_pinned()
    {
        // Renaming any of these silently breaks every operator who tuned a gateway model / a slow deployment via env.
        LlmModelCapabilities.NoSamplingModelsEnvVar.ShouldBe("CODESPACE_LLM_NO_SAMPLING_MODELS");
        LlmModelCapabilities.MaxCompletionTokensModelsEnvVar.ShouldBe("CODESPACE_LLM_MAX_COMPLETION_TOKENS_MODELS");
        LlmModelCapabilities.DefaultMaxOutputTokensEnvVar.ShouldBe("CODESPACE_LLM_DEFAULT_MAX_OUTPUT_TOKENS");
    }
}
