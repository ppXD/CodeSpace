using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Constants;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Nodes;

/// <summary>
/// A2: the MODEL-PLANE OUTAGE park, for any node that calls a model. A transient/rate-limited fault no longer
/// kills the run — the node parks on the shared exponential ladder (1m → 5m → 15m → 60m, ±20% jitter, anchored at
/// the first park) and the wake re-enters the same node with the ladder position intact; once the whole 24h window
/// has elapsed the node fails honestly rather than parking forever.
///
/// <para>The supervisor lane has ridden this ladder since P1.1 while the planner and synthesizer nodes had NO
/// retry policy and NO park at all — so the same provider blip that a Deep run sleeps through killed a Standard
/// run in minutes. This is the same ladder, not a second one: <see cref="SupervisorInfraPark"/> is already pure
/// statics over (resume payload, now) with no supervisor types, no clock and no DB, and its marker read is
/// self-identifying, so a ladder only ever continues from its own wakes.</para>
///
/// <para>What is deliberately NOT shared is the ending: the supervisor's window-exhausted arm writes an honest
/// degraded stop through its decision ledger, which a generic node has no business doing. Here the window ends in
/// a plain node failure — the run's own error, on the node that could not reach a model.</para>
///
/// <para><b>Do not pair this with a Retry policy on the same node.</b> The engine counts durable
/// <c>attempt.failed</c> records as prior attempts whenever a resume payload is present, so a node with both would
/// burn its retry budget across park wakes and fail early — which is why this ships as the ONLY resilience on
/// nodes that had none, rather than as a second layer over one.</para>
/// </summary>
public static class InfraPark
{
    /// <summary>
    /// Whether a model-plane fault is worth parking for — the catch FILTER, so it must stay pure and cheap. An
    /// auth failure or a model-side miss is operator-actionable NOW and parking would only hide it, so those fall
    /// through to the node's ordinary failure path.
    /// </summary>
    public static bool IsParkable(LlmApiException fault) => SupervisorInfraPark.IsParkable(fault.Category);

    /// <summary>
    /// The park, or the honest failure once the whole window is spent. Call ONLY from a catch body guarded by
    /// <see cref="IsParkable"/> — never from the filter itself: the ladder's delay carries jitter, so evaluating
    /// it twice would compute one delay and park on another.
    /// </summary>
    public static NodeResult Park(NodeRunContext context, LlmApiException fault, DateTimeOffset now)
    {
        var state = SupervisorInfraPark.Next(context.ResumePayload, now);

        if (state.WindowExhausted)
        {
            context.Logger.LogWarning("Node {NodeId}: the model plane stayed unavailable past the whole {Window} park window — failing the node honestly", context.NodeId, SupervisorInfraPark.MaxParkWindow);

            return NodeResult.Fail($"The model plane stayed unavailable for {SupervisorInfraPark.MaxParkWindow.TotalHours:0}h: {fault.Message}", retryable: false);
        }

        var delay = SupervisorInfraPark.DelayFor(state.Parks);
        var marker = SupervisorInfraPark.Marker(state, fault.Message);

        context.Logger.LogWarning("Node {NodeId}: model call hit a {Category} infra fault — parking {Delay} (park {Parks} since {First:o}) instead of failing the run", context.NodeId, fault.Category, delay, state.Parks, state.FirstParkedAtUtc);

        return NodeResult.Suspend(new SuspensionToken
        {
            Kind = WorkflowWaitKinds.SupervisorInfraPark,
            // IterationKey deliberately UNSET: the engine then keys the wait on the node's AMBIENT cell, so a
            // parked node inside a map branch keeps its branch identity (the supervisor overrides it only because
            // its own node is top-level). Re-parking drops the prior, already-resolved wait, which is the engine's
            // documented re-suspend behaviour.
            Payload = marker,
            DeadlineAt = now + delay,
            TimeoutPayload = marker,
        });
    }
}
