using CodeSpace.Core.Services.Workflows.Nodes.Builtin;

namespace CodeSpace.Core.Services.Workflows.Plugins.Builtin;

/// <summary>
/// Generic HTTP toolset — universal escape hatch for calling any API the engine doesn't
/// have a bespoke node for. Independent of the git / llm / core-flow domains so an operator
/// can disable it without affecting other workflows.
/// </summary>
public sealed class HttpToolsPlugin : IPluginModule
{
    public string Name => "HTTP tools";

    public IReadOnlyList<Type> Nodes { get; } = new[]
    {
        typeof(HttpRequestNode),
    };

    public IReadOnlyList<Type> RunSourceMatchers { get; } = Array.Empty<Type>();
    public IReadOnlyList<Type> AuxiliaryServices { get; } = Array.Empty<Type>();
}
