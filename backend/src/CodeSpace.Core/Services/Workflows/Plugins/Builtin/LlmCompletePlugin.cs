using CodeSpace.Core.Services.Workflows.Nodes.Builtin;

namespace CodeSpace.Core.Services.Workflows.Plugins.Builtin;

/// <summary>
/// LLM-completion node + (in future) embedding / vision / structured-output nodes that all
/// ride on the <c>ILLMClientRegistry</c>. Removing this plugin disables AI features cleanly
/// but the engine still runs git / http / logic flows.
/// </summary>
public sealed class LlmCompletePlugin : IPluginModule
{
    public string Name => "LLM";

    public IReadOnlyList<Type> Nodes { get; } = new[]
    {
        typeof(LlmCompleteNode),
    };

    public IReadOnlyList<Type> RunSourceMatchers { get; } = Array.Empty<Type>();
    public IReadOnlyList<Type> AuxiliaryServices { get; } = Array.Empty<Type>();
}
