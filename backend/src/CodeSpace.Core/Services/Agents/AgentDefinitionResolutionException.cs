namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Thrown when an <c>agent.run</c> run references an Agent persona that can't be resolved — the persona
/// doesn't exist for the run's team (or was soft-deleted), or the merge produces nothing to run. The
/// workflow engine maps it to a clean node failure (mirrors how <c>WorkspaceException</c> fails a run).
/// </summary>
public sealed class AgentDefinitionResolutionException : Exception
{
    public AgentDefinitionResolutionException(string message) : base(message) { }

    public AgentDefinitionResolutionException(string message, Exception innerException) : base(message, innerException) { }
}
