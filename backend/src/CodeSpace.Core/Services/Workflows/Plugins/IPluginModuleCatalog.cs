namespace CodeSpace.Core.Services.Workflows.Plugins;

/// <summary>
/// Runtime registry of every loaded <see cref="IPluginModule"/>. Drives the
/// /api/workflows/plugins endpoint and lets tests assert which plugins are wired in.
/// </summary>
public interface IPluginModuleCatalog
{
    IReadOnlyList<IPluginModule> Modules { get; }
}
