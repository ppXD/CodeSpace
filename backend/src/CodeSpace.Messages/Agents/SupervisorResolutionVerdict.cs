namespace CodeSpace.Messages.Agents;

/// <summary>
/// The build/test VERDICT of a resolver agent's reconciliation (resolver loop #379, S3) — the instruction-encoded
/// signal (fork 5, c-2a) the supervisor reads off the resolver agent's terminal result to decide whether the
/// resolution may be ACCEPTED (S4 gates on it). The resolver's recipe instructs it to commit + end its summary with
/// the verified marker ONLY when the build + full test suite pass on the reconciled result; this verdict is the
/// read of that signal, never the model's say-so.
/// </summary>
public enum SupervisorResolutionVerdict
{
    /// <summary>No resolver agent has terminated yet (the resolve is still parked, or the outcome carries no folded result) — the verdict isn't known.</summary>
    Unknown,

    /// <summary>The resolver agent succeeded AND its summary carries the verified marker — the build + tests passed on the reconciliation. The ONLY verdict S4 may accept on.</summary>
    Verified,

    /// <summary>The resolver agent terminated but did NOT signal a verified pass (it failed, was cancelled, or its summary lacks the marker) — the reconciliation is NOT safe to accept; fall back fail-safe (retry within the cap, or leave the conflict for a human).</summary>
    Unverified,
}
