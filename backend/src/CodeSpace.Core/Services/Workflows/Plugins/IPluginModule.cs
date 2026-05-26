using CodeSpace.Core.Services.Workflows.RunSources;

namespace CodeSpace.Core.Services.Workflows.Plugins;

/// <summary>
/// Pure descriptor for a workflows-side plugin. Mirrors <c>IProviderModule</c> exactly —
/// one class per plugin, lists every node / run-source matcher / auxiliary service the plugin
/// contributes. Adding a new plugin = one new class implementing this interface; the DI
/// module discovers and registers everything.
///
/// New plugins (slack, jira, http, schedule, etc.) drop in via new modules — zero engine edits.
/// </summary>
public interface IPluginModule
{
    /// <summary>Stable display name. Lives in the operator-visible plugin manager.</summary>
    string Name { get; }

    /// <summary>Classes implementing <c>INodeRuntime</c>. One entry per node type.</summary>
    IReadOnlyList<Type> Nodes { get; }

    /// <summary>Classes implementing <see cref="IRunSourceMatcher"/>. One entry per matcher.</summary>
    IReadOnlyList<Type> RunSourceMatchers { get; }

    /// <summary>Plugin-private services (LLM client, HTTP wrapper, etc) that nodes consume via constructor injection.</summary>
    IReadOnlyList<Type> AuxiliaryServices { get; }
}
