namespace CodeSpace.Core.Services.Agents;

/// <summary>Applies the run's harness allow-list consistently to authoring, reconciliation, and dispatch.</summary>
public static class AgentHarnessPool
{
    public static IReadOnlyList<IAgentHarness> Clamp(IReadOnlyList<IAgentHarness> registered, IReadOnlyList<string>? allowedKinds)
    {
        if (allowedKinds is not { Count: > 0 }) return registered;

        var allowed = allowedKinds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return registered.Where(h => allowed.Contains(h.Kind)).ToList();
    }
}
