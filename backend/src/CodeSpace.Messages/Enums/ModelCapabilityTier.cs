namespace CodeSpace.Messages.Enums;

/// <summary>
/// A coarse, cached CODING-capability tier for a <c>ModelCredentialModel</c>, inferred by the brain from the model id
/// alone (frontier / strong / basic) and surfaced in the capability catalog so the supervisor / planner can allocate a
/// stronger model to a harder subtask. <see cref="Unknown"/> is the default sentinel: a not-yet-tiered model, OR an
/// opaque / renamed gateway id the brain could not recognise (the hook for a later objective probe slice).
///
/// <para>An ADVISORY render/allocation hint, never a selection gate — the pool stays capability-generic (a per-model
/// capability column was deliberately dropped in migration 0073). The ascending order (Unknown &lt; Basic &lt; Strong &lt;
/// Frontier) lets a later auto-ordering slice rank by tier; <see cref="Unknown"/> is 0 so an un-tiered pool defaults to it.</para>
/// </summary>
public enum ModelCapabilityTier
{
    Unknown,
    Basic,
    Strong,
    Frontier,
}
