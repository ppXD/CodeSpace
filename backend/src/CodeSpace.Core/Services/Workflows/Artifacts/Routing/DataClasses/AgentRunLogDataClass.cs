using CodeSpace.Core.Services.Agents.AgentRunLogging;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Routing.DataClasses;

/// <summary>
/// Captured Agent Run logs. Unlike the workflow-artifact class this one has no local backend, so its route is load
/// bearing from the moment capture starts. The key is taken from the resolver that reads it.
/// </summary>
public sealed class AgentRunLogDataClass : IRoutedDataClass
{
    public string TypeKey => AgentRunLogStorageResolver.DataClassTypeKey;

    public string DisplayName => "Agent run logs";
}
