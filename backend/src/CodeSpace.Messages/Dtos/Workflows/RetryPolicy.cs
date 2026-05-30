namespace CodeSpace.Messages.Dtos.Workflows;

/// <summary>
/// Optional per-node retry-on-failure policy. A <c>null</c> <see cref="NodeDefinition.Retry"/>
/// means "run the node exactly once" — the pre-Phase-2 behaviour — so adding this field is
/// non-breaking: existing definitions omit it, and <c>DefinitionHash</c> drops null properties
/// so their content hash is unchanged.
///
/// <para>When set, the engine re-runs the node after a <b>genuine failure</b> (a returned
/// <c>NodeResult.Fail</c> OR a thrown exception) up to <see cref="MaxAttempts"/> total attempts,
/// waiting <see cref="BackoffSeconds"/> between attempts. A success/skip on any attempt stops the
/// loop and the run proceeds normally.</para>
///
/// <para><b>Never retried:</b> a suspend (timer / approval / callback) and an operator cancel.
/// Those aren't failures — they pause or stop the run by design.</para>
///
/// <para><b>In-process + bounded:</b> the backoff is awaited on the worker, so the policy is for
/// transient blips (a flaky 503, a lock conflict, a momentary network drop) — not long waits. The
/// engine clamps both the attempt count and the per-attempt delay (see the caps on the engine's
/// retry plan) so a mis-set policy can't pin a worker. For long durable waits, use
/// <c>flow.sleep</c>, which suspends the run instead of blocking.</para>
///
/// <para>Lenient by design: members carry safe defaults rather than <c>required</c>, so a
/// hand-authored partial JSON block (e.g. <c>{"maxAttempts":3}</c>) deserialises cleanly. The
/// engine clamps; <c>DefinitionValidator</c> surfaces out-of-range values at save time.</para>
/// </summary>
public sealed record RetryPolicy
{
    /// <summary>Total attempts including the first. <c>1</c> = no retry. Clamped to <c>[1, cap]</c> by the engine.</summary>
    public int MaxAttempts { get; init; } = 1;

    /// <summary>Seconds to wait between attempts. <c>0</c> = retry immediately. Clamped to <c>[0, cap]</c> by the engine.</summary>
    public double BackoffSeconds { get; init; }
}
