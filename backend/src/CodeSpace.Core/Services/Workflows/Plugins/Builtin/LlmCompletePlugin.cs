using CodeSpace.Core.Services.Workflows.Nodes.Builtin;

namespace CodeSpace.Core.Services.Workflows.Plugins.Builtin;

/// <summary>
/// The in-process AI nodes — LLM completion + the AI planning node — that ride on the LLM plane
/// (<c>ILLMClientRegistry</c> / the structured-output clients). Removing this plugin disables AI features
/// cleanly but the engine still runs git / http / logic flows.
/// </summary>
public sealed class LlmCompletePlugin : IPluginModule
{
    public string Name => "LLM";

    public IReadOnlyList<Type> Nodes { get; } = new[]
    {
        typeof(LlmCompleteNode),
        typeof(PlanAuthorNode),
    };

    public IReadOnlyList<Type> RunSourceMatchers { get; } = Array.Empty<Type>();
    public IReadOnlyList<Type> AuxiliaryServices { get; } = Array.Empty<Type>();
}
