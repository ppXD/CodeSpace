namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>
/// Descriptor for one LLM provider — same shape as <c>IProviderModule</c> + <c>IPluginModule</c>.
/// Lists the client class + any auxiliary services (rate limiter, request-builder helpers).
/// Adding a new LLM provider = one new module class, no engine edits.
/// </summary>
public interface ILLMProviderModule
{
    /// <summary>Stable provider tag — "Anthropic", "OpenAI", "Bedrock", etc.</summary>
    string Provider { get; }

    /// <summary>The class implementing <c>ILLMClient</c> for this provider.</summary>
    Type Client { get; }

    /// <summary>Default model id surfaced as the form default in the editor. Per-call override always wins.</summary>
    string DefaultModel { get; }

    /// <summary>Per-provider auxiliary services nodes/plugin may consume.</summary>
    IReadOnlyList<Type> AuxiliaryServices { get; }
}
