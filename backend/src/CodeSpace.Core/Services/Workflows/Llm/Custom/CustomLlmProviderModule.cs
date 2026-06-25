namespace CodeSpace.Core.Services.Workflows.Llm.Custom;

/// <summary>
/// Registers the <see cref="CustomClient"/> as a sibling provider (Rule 18.3): the DI scan
/// (<c>CodeSpaceModule.RegisterLLMProviderModules</c>) discovers every <see cref="ILLMProviderModule"/> and wires its
/// <see cref="Client"/> as both <see cref="ILLMClient"/> and <see cref="IStructuredLLMClient"/>, so the
/// <c>LLMClientRegistry</c> serves a <c>"Custom"</c>-provider credential — an operator's own OpenAI-compatible gateway —
/// with no further wiring. With this module registered, <c>"Custom"</c> becomes an eligible IN-PROCESS structured
/// provider, so a Custom-tagged pool model can run the supervisor brain / planner / effort classifier, not just the
/// agent CLI harness.
/// </summary>
public sealed class CustomLlmProviderModule : ILLMProviderModule
{
    public string Provider => "Custom";

    public Type Client => typeof(CustomClient);

    /// <summary>No meaningful default for an arbitrary custom gateway — the operator names the model id per credentialed-model row. A placeholder hint surfaces in the editor; the per-row id always wins.</summary>
    public string DefaultModel => "custom-model";

    public IReadOnlyList<Type> AuxiliaryServices { get; } = Array.Empty<Type>();
}
