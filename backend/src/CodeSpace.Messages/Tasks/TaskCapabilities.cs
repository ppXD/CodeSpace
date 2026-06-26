namespace CodeSpace.Messages.Tasks;

/// <summary>
/// The OPEN-STRING capability kinds a task recipe's projection may DECLARE it needs at execution
/// (<c>ITaskRecipe.RequiresCapability</c>) and an <c>ICapabilityProbe</c> reports availability for. Consts (NOT an
/// enum, Rule 18.1 / Rule 8 — owns the wire string) so a new capability is a new const + a new probe folder, never a
/// core-enum edit. The router degrades to a recipe's fallback when the named capability is unavailable.
///
/// <para>NONE are currently declared — the supervisor lane (the only former capability) graduated its feature gate and
/// is always on, so its recipe no longer degrades. The generic seam remains for a future capability that needs it.</para>
/// </summary>
public static class TaskCapabilities
{
}
