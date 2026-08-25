namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Narrows a registry to the run's harness allow-list. One helper, three call sites, so the list means the same thing at
/// every point a harness is chosen: the capability catalog the supervisor is shown (<c>LlmSupervisorDecider</c>), the
/// spawn-time compatibility clamp (<c>RealSupervisorActionExecutor.ApplyDispatchModelAsync</c>), and the EXECUTION-time
/// repair (<c>HarnessModelReconciler</c>, via the list carried on <c>AgentTask.AllowedHarnessKinds</c>) — that last one
/// is why the list is not merely an authoring-time suggestion.
/// <para>Null / empty <paramref name="allowedKinds"/> returns the registry unchanged (unbounded). It only FILTERS: an
/// allowed kind nothing registers simply matches nothing, and a caller left with an empty list decides for itself what
/// that means.</para>
/// </summary>
public static class AgentHarnessPool
{
    public static IReadOnlyList<IAgentHarness> Clamp(IReadOnlyList<IAgentHarness> registered, IReadOnlyList<string>? allowedKinds)
    {
        if (allowedKinds is not { Count: > 0 }) return registered;

        var allowed = allowedKinds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return registered.Where(h => allowed.Contains(h.Kind)).ToList();
    }
}
