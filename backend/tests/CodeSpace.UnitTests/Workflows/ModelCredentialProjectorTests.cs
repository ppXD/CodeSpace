using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The universal invariants every <see cref="IModelCredentialProjector"/> must satisfy, regardless of which
/// harness implements it: it advertises providers, maps a credential into a non-empty env with the key
/// landing under SOME var (never blank, never in argv — env only), rejects an unsupported provider, and
/// tolerates a keyless credential. Each harness's specific var names + gateway-vs-direct nuance live in its
/// own concrete subclass below.
/// </summary>
public abstract class ModelCredentialProjectorContractTests
{
    protected abstract IModelCredentialProjector Projector { get; }

    [Fact]
    public void SupportedProviders_is_non_empty() => Projector.SupportedProviders.ShouldNotBeEmpty();

    [Fact]
    public void Every_supported_provider_injects_the_key_into_a_non_empty_env()
    {
        foreach (var provider in Projector.SupportedProviders)
        {
            var env = Projector.ProjectToEnv(new ResolvedModelCredential { Provider = provider, ApiKey = "sk-secret-xyz", BaseUrl = "https://gw.example/v1" });

            env.ShouldNotBeEmpty($"provider '{provider}' must map to at least one env var");
            env.Values.ShouldAllBe(v => !string.IsNullOrEmpty(v), $"provider '{provider}' must never emit a blank env value");
            env.Values.ShouldContain("sk-secret-xyz", $"the api key must be injected under some env var for provider '{provider}'");
        }
    }

    [Fact]
    public void An_unsupported_provider_throws() =>
        Should.Throw<ArgumentException>(() => Projector.ProjectToEnv(new ResolvedModelCredential { Provider = "definitely-not-a-real-provider", ApiKey = "k" }));

    [Fact]
    public void A_keyless_credential_emits_no_blank_values()
    {
        // A keyless provider (a local model reached over a base URL) must not crash and must not emit an empty value.
        var env = Projector.ProjectToEnv(new ResolvedModelCredential { Provider = Projector.SupportedProviders[0], ApiKey = null, BaseUrl = "http://localhost:1234" });

        env.Values.ShouldAllBe(v => !string.IsNullOrEmpty(v));
    }
}

[Trait("Category", "Unit")]
public sealed class CodexModelCredentialProjectorTests : ModelCredentialProjectorContractTests
{
    private readonly CodexHarness _harness = new();
    protected override IModelCredentialProjector Projector => _harness;

    [Fact]
    public void Env_var_names_are_pinned()
    {
        // The agent authenticates with whatever lands under these names; renaming one silently breaks model auth.
        CodexHarness.ApiKeyEnvVar.ShouldBe("OPENAI_API_KEY");
        CodexHarness.BaseUrlEnvVar.ShouldBe("OPENAI_BASE_URL");
    }

    [Fact]
    public void OpenAI_maps_the_key_to_OPENAI_API_KEY_only()
    {
        var env = _harness.ProjectToEnv(new ResolvedModelCredential { Provider = "OpenAI", ApiKey = "sk-openai" });

        env[CodexHarness.ApiKeyEnvVar].ShouldBe("sk-openai");
        env.ShouldNotContainKey(CodexHarness.BaseUrlEnvVar);   // no base URL supplied → not emitted
    }

    [Fact]
    public void A_base_url_provider_emits_both_key_and_base_url()
    {
        var env = _harness.ProjectToEnv(new ResolvedModelCredential { Provider = "OpenRouter", ApiKey = "sk-or", BaseUrl = "https://openrouter.ai/api/v1" });

        env[CodexHarness.ApiKeyEnvVar].ShouldBe("sk-or");
        env[CodexHarness.BaseUrlEnvVar].ShouldBe("https://openrouter.ai/api/v1");
    }

    [Fact]
    public void A_keyless_local_provider_emits_only_the_base_url()
    {
        var env = _harness.ProjectToEnv(new ResolvedModelCredential { Provider = "Ollama", ApiKey = null, BaseUrl = "http://localhost:11434/v1" });

        env.ShouldNotContainKey(CodexHarness.ApiKeyEnvVar);
        env[CodexHarness.BaseUrlEnvVar].ShouldBe("http://localhost:11434/v1");
    }
}

[Trait("Category", "Unit")]
public sealed class ClaudeModelCredentialProjectorTests : ModelCredentialProjectorContractTests
{
    private readonly ClaudeCodeHarness _harness = new();
    protected override IModelCredentialProjector Projector => _harness;

    [Fact]
    public void Env_var_names_are_pinned()
    {
        ClaudeCodeHarness.ApiKeyEnvVar.ShouldBe("ANTHROPIC_API_KEY");
        ClaudeCodeHarness.BaseUrlEnvVar.ShouldBe("ANTHROPIC_BASE_URL");
        ClaudeCodeHarness.AuthTokenEnvVar.ShouldBe("ANTHROPIC_AUTH_TOKEN");
    }

    [Fact]
    public void Direct_anthropic_uses_the_api_key_var_not_the_auth_token()
    {
        var env = _harness.ProjectToEnv(new ResolvedModelCredential { Provider = "Anthropic", ApiKey = "sk-ant" });

        env[ClaudeCodeHarness.ApiKeyEnvVar].ShouldBe("sk-ant");
        env.ShouldNotContainKey(ClaudeCodeHarness.AuthTokenEnvVar);
        env.ShouldNotContainKey(ClaudeCodeHarness.BaseUrlEnvVar);   // no base URL → official endpoint
    }

    [Fact]
    public void A_gateway_uses_the_auth_token_and_base_url_never_the_official_api_key()
    {
        var env = _harness.ProjectToEnv(new ResolvedModelCredential { Provider = "Custom", ApiKey = "gw-token", BaseUrl = "https://gateway.internal/anthropic" });

        env[ClaudeCodeHarness.AuthTokenEnvVar].ShouldBe("gw-token");
        env[ClaudeCodeHarness.BaseUrlEnvVar].ShouldBe("https://gateway.internal/anthropic");
        env.ShouldNotContainKey(ClaudeCodeHarness.ApiKeyEnvVar);   // a gateway token must never be presented as the official Anthropic key
    }
}

/// <summary>
/// The cross-harness distinction, pinned: the HARNESS picks the wire format (Codex → OpenAI, Claude →
/// Anthropic), the credential's PROVIDER tag picks which harnesses accept it + the auth scheme, and the
/// base URL just overrides the endpoint. So one operator with a single endpoint that speaks BOTH formats
/// uses a "Custom"-tagged credential, and it projects correctly under either harness — OpenAI-shaped env
/// for Codex, Anthropic-bearer env for Claude — from the exact same stored credential.
/// </summary>
[Trait("Category", "Unit")]
public sealed class CustomGatewayDualFormatTests
{
    private static readonly ResolvedModelCredential Custom = new() { Provider = "Custom", ApiKey = "sk-gateway-virtual-key", BaseUrl = "https://my-gateway/v1" };

    [Fact]
    public void Both_harnesses_accept_a_Custom_credential()
    {
        new CodexHarness().SupportedProviders.ShouldContain("Custom");
        new ClaudeCodeHarness().SupportedProviders.ShouldContain("Custom");
    }

    [Fact]
    public void Codex_projects_a_Custom_credential_as_OpenAI_shaped_env()
    {
        var env = new CodexHarness().ProjectToEnv(Custom);

        env[CodexHarness.ApiKeyEnvVar].ShouldBe("sk-gateway-virtual-key");   // OPENAI_API_KEY (Bearer)
        env[CodexHarness.BaseUrlEnvVar].ShouldBe("https://my-gateway/v1");   // OPENAI_BASE_URL — carrier; BuildInvocation keeps /v1 (idempotent)
    }

    [Fact]
    public void Claude_projects_the_same_Custom_credential_as_Anthropic_bearer_env()
    {
        var env = new ClaudeCodeHarness().ProjectToEnv(Custom);

        env[ClaudeCodeHarness.AuthTokenEnvVar].ShouldBe("sk-gateway-virtual-key");   // ANTHROPIC_AUTH_TOKEN (Bearer), not x-api-key
        // The SAME stored /v1 base URL is normalized to the ROOT for Claude (its SDK appends /v1/messages), so the one
        // Custom credential connects under BOTH harnesses — Codex keeps /v1, Claude strips it. This is the dual-format fix.
        env[ClaudeCodeHarness.BaseUrlEnvVar].ShouldBe("https://my-gateway");
        env.ShouldNotContainKey(ClaudeCodeHarness.ApiKeyEnvVar);
    }
}
