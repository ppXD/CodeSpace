namespace CodeSpace.Core.Services.Workflows.Plugins;

public sealed class PluginModuleCatalog : IPluginModuleCatalog
{
    public PluginModuleCatalog(IEnumerable<IPluginModule> modules)
    {
        Modules = modules.ToList();
    }

    public IReadOnlyList<IPluginModule> Modules { get; }
}
