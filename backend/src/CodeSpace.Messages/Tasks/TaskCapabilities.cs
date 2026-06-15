namespace CodeSpace.Messages.Tasks;

/// <summary>
/// The OPEN-STRING capability kinds a task recipe's projection may DECLARE it needs at execution
/// (<c>ITaskRecipe.RequiresCapability</c>) and an <c>ICapabilityProbe</c> reports availability for. Consts (NOT
/// an enum, Rule 18.1 / Rule 8 — owns the wire string) so a new capability is a new const + a new probe folder,
/// never a core-enum edit, and a rename is a compile-time-visible decision. The router degrades to a recipe's
/// fallback when the named capability is unavailable; the projection's OWN execution-time gate (e.g. the
/// <c>agent.supervisor</c> node's lane check) remains the real enforcer — the capability gate is a routing UX
/// nicety, not the security boundary.
/// </summary>
public static class TaskCapabilities
{
    /// <summary>The bounded durable supervisor lane (<c>CODESPACE_SUPERVISOR_LANE_ENABLED</c>) the <c>supervisor</c> recipe's projection needs — its probe reads <c>SupervisorLane.IsEnabled</c>.</summary>
    public const string SupervisorLane = "supervisor-lane";
}
