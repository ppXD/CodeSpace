namespace CodeSpace.Core.Services.Providers.Resilience;

/// <summary>
/// The agent run on whose behalf provider requests are being made on this async path, so the singleton
/// <see cref="ExternalCallResilience"/> can charge each request to that run's share of the connection
/// (<see cref="ExternalCallResilience.TokensPerMinutePerAgentRun"/>). The MCP handler enters it around every tool call an
/// agent makes, so whatever the call reads — a node's list, an approval card's pull request — is charged without any
/// provider method naming the run. Absent (the Pulls tab, a workflow node, a webhook) ⇒ no share applies. Flows across
/// awaits via <see cref="AsyncLocal{T}"/>; the prior value is restored on dispose.
/// </summary>
public static class AgentRunProviderScope
{
    private static readonly AsyncLocal<Guid?> Run = new();

    /// <summary>The agent run whose requests these are, or null outside any agent's tool call.</summary>
    public static Guid? Current => Run.Value;

    /// <summary>Charge the provider requests made inside the using-block to <paramref name="runId"/>; restores the prior run on dispose.</summary>
    public static IDisposable Enter(Guid runId)
    {
        var prior = Run.Value;
        Run.Value = runId;

        return new Exit(prior);
    }

    private sealed class Exit(Guid? prior) : IDisposable
    {
        public void Dispose() => Run.Value = prior;
    }
}
