namespace CodeSpace.Core.Services.Workflows.Llm.OpenAi;

/// <summary>
/// Registers the OpenAI-compatible client as a sibling provider (Rule 18.3): the DI scan
/// (<c>CodeSpaceModule.RegisterLLMProviderModules</c>) discovers every <see cref="ILLMProviderModule"/> and wires
/// its <see cref="Client"/> as both <see cref="ILLMClient"/> and <see cref="IStructuredLLMClient"/>, so the
/// <c>LLMClientRegistry</c> serves an "OpenAI"-provider credential with no further wiring. Adding this module
/// alongside the Anthropic one makes BOTH wire formats selectable purely by a credential's Provider tag.
/// </summary>
public sealed class OpenAiLlmProviderModule : ILLMProviderModule
{
    public string Provider => "OpenAI";

    public Type Client => typeof(OpenAiClient);

    /// <summary>A sensible default model id for an OpenAI-wire gateway; an operator overrides it per credential / model row.</summary>
    public string DefaultModel => "gpt-4o";

    public IReadOnlyList<Type> AuxiliaryServices { get; } = Array.Empty<Type>();
}
