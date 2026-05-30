using CodeSpace.Messages.Dtos.Workflows;

namespace CodeSpace.Core.Services.Workflows.Engine;

/// <summary>
/// The clamped, runtime form of a node's <see cref="RetryPolicy"/>. The DTO is lenient
/// (operator-authored, possibly out of range); this is the validated shape the engine's retry
/// loop trusts. Separating them keeps the clamping in one tested place and out of the DTO.
///
/// <para>The caps are deliberate safety bounds, not features: retries are awaited in-process on
/// the worker, so an unbounded attempt count or backoff would pin a worker thread. A policy that
/// needs more is a design smell — long waits belong on <c>flow.sleep</c> (which suspends), and
/// more than a handful of immediate retries rarely helps a genuinely-down dependency.</para>
/// </summary>
public readonly record struct RetryPlan(int MaxAttempts, double BackoffSeconds)
{
    /// <summary>Hard ceiling on total attempts. A policy above this is clamped down (and rejected at save by the validator).</summary>
    public const int MaxAttemptsCap = 10;

    /// <summary>Hard ceiling on the per-attempt backoff, in seconds. Keeps an in-process wait bounded.</summary>
    public const double MaxBackoffSeconds = 60;

    /// <summary>The no-retry plan: one attempt, no wait. What a <c>null</c> policy resolves to.</summary>
    public static RetryPlan None => new(1, 0);

    /// <summary>
    /// Clamp a policy into the valid range. <c>null</c> → <see cref="None"/>. Attempts clamp to
    /// <c>[1, MaxAttemptsCap]</c>; backoff clamps to <c>[0, MaxBackoffSeconds]</c> (NaN / negative
    /// collapse to 0). The engine calls this so even a definition that bypassed save-time
    /// validation (hand-edited DB row, older JSON) can never produce an unbounded loop.
    /// </summary>
    public static RetryPlan From(RetryPolicy? policy)
    {
        if (policy == null) return None;

        var attempts = Math.Clamp(policy.MaxAttempts, 1, MaxAttemptsCap);
        var backoff = policy.BackoffSeconds is > 0 and <= MaxBackoffSeconds ? policy.BackoffSeconds
            : policy.BackoffSeconds > MaxBackoffSeconds ? MaxBackoffSeconds
            : 0;

        return new RetryPlan(attempts, backoff);
    }

    /// <summary>True when more than one attempt is allowed — i.e. the node will be retried on failure.</summary>
    public bool RetriesOnFailure => MaxAttempts > 1;

    /// <summary>The wait before the next attempt. Fixed across attempts (no exponential backoff in v1).</summary>
    public TimeSpan Delay => TimeSpan.FromSeconds(BackoffSeconds);
}
