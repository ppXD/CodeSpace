using CodeSpace.Core.Services.Agents.AgentRunLogging;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Routing.DataClasses;

/// <summary>
/// Captured Agent Run logs. Unlike the workflow-artifact class this one has no local backend, so its route is load
/// bearing from the moment capture starts. The key is taken from the resolver that reads it.
///
/// <para>It deliberately does NOT declare <see cref="IRoutedDataClassLocalFallback"/>: with nowhere else to put the
/// bytes, a route that was never activated has to stay a typed refusal rather than become dropped capture.</para>
/// </summary>
public sealed class AgentRunLogDataClass : IRoutedDataClass
{
    public string TypeKey => AgentRunLogStorageResolver.DataClassTypeKey;

    public string DisplayName => "Agent run logs";
}
