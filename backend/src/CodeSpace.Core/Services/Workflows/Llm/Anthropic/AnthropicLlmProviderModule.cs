namespace CodeSpace.Core.Services.Workflows.Llm.Anthropic;

public sealed class AnthropicLlmProviderModule : ILLMProviderModule
{
    public string Provider => "Anthropic";

    public Type Client => typeof(AnthropicClient);

    /// <summary>Sensible default — Sonnet 4.5 is the right speed/quality balance for code review.</summary>
    public string DefaultModel => "claude-sonnet-4-5";

    public IReadOnlyList<Type> AuxiliaryServices { get; } = Array.Empty<Type>();
}
