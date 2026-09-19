using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Exceptions;

namespace CodeSpace.Core.Services.Workflows.Engine;

/// <summary>
/// "Park, don't die" for the act-as-user seam. A node whose manifest declares <c>ActsAsUser</c> can only
/// authenticate as one specific person's own provider token; when that person has no live linked identity the
/// enforcement seam throws <see cref="ActorIdentityRequiredException"/>. That used to KILL the run in the
/// background — a chat card click returned 204 and the run died a minute later with "submit_review failed", with
/// nothing anyone could do but start over. It now parks on a <see cref="WorkflowWaitKinds.ActorIdentityLink"/>
/// wait whose <c>DeadlineAt</c> walks the <see cref="Schedule"/> poll ladder, and the wake simply re-runs the node
/// — which succeeds the moment the identity exists. Once the whole <see cref="MaxParkWindow"/> has elapsed the
/// node fails honestly instead of parking forever.
///
/// <para><b>Why a poll and not a link EVENT.</b> What the node needs is not "a row was inserted" but
/// <c>IActorIdentityResolver.ResolveAsync</c> returning non-null — which also depends on the backing credential
/// still being Active. Re-running the node asks that exact question, so the run heals no matter HOW the identity
/// became usable: a PAT link, an OAuth connect, a re-pointed identity, an operator re-activating a revoked
/// credential. An insert hook on the two link paths would cover strictly less and would have to fire after the
/// caller's transaction commits to avoid resuming into a read that cannot see the row yet.</para>
///
/// <para><b>Why re-running is safe.</b> The identity is resolved BEFORE the provider write in every act-as-user
/// service path (you cannot make the call without the credential), so this exception PROVES the external side
/// effect never fired. A wake therefore re-runs a node that has done nothing — never a second review, a second
/// merge, a second comment.</para>
///
/// <para><b>The park is transparent.</b> The marker MERGES OVER the resume payload it interrupted rather than
/// replacing it, so whatever brought the node here survives the park. That matters for a from-node rerun: the
/// side-effect gate reads <c>approved: true</c> off the resume payload, and a park that dropped it would make the
/// wake look un-approved and SKIP the node — silently losing the write the operator had just approved.</para>
///
/// <para>Pure statics over (resume payload, fault, now) — no clock, no DB, no engine types — so the whole ladder
/// is unit-pinned directly. Deliberately NOT folded into <see cref="Supervisor.SupervisorInfraPark"/>: that ladder
/// rides out a provider OUTAGE (minutes to hours, nothing a human can shorten) while this one waits on a PERSON
/// who may act in the next thirty seconds, so every constant differs, and that type's name and marker are durable
/// values written into rows.</para>
/// </summary>
public static class ActorIdentityPark
{
    /// <summary>
    /// The poll ladder; park N waits Schedule[min(N−1, last)] (±20% jitter, so a fleet of parked runs never wakes
    /// in lockstep). Tight at the head because the thing being waited on is a person who was just asked and may
    /// link within the minute; widening out so a run parked overnight costs a handful of wakes, not hundreds.
    /// Pinned by a unit test (Rule 8).
    /// </summary>
    public static readonly TimeSpan[] Schedule = { TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(60) };

    /// <summary>The TOTAL window one run waits for the link before it fails honestly — measured from the FIRST park, so a run can never park forever. Pinned by a unit test (Rule 8).</summary>
    public static readonly TimeSpan MaxParkWindow = TimeSpan.FromHours(24);

    /// <summary>The self-identifying marker field on the park's resume payload — how a re-entry tells this park's wake from every other resume shape.</summary>
    public const string MarkerField = "actorIdentityLink";

    /// <summary>
    /// The park, or the honest failure once the whole window is spent.
    ///
    /// <para><paramref name="priorState"/> is THIS park's last marker, which the caller reads from the node's own
    /// durable wait row (null on a first reach). It must NOT be the engine's injected resume payload: that is
    /// populated from RESOLVED waits only, so any re-dispatch while the park is still Pending would hand this
    /// method null and silently restart the ladder with a fresh 24h anchor — a bound that is not a bound.
    /// <c>WorkflowEngine.ReadPriorParkStateAsync</c> owns that read and an integration test holds the line.</para>
    ///
    /// <para>Whatever it carries is preserved into the next marker, so state the node was resumed with rides
    /// forward across every park.</para>
    /// </summary>
    public static NodeResult Park(JsonElement? priorState, ActorIdentityRequiredException fault, DateTimeOffset now)
    {
        var state = Next(priorState, now);

        if (state.WindowExhausted) return NodeResult.Fail(ExhaustedMessage(fault), retryable: false);

        var marker = Marker(priorState, state, fault);

        return NodeResult.Suspend(new SuspensionToken
        {
            Kind = WorkflowWaitKinds.ActorIdentityLink,
            // IterationKey deliberately UNSET: the engine then keys the wait on the node's AMBIENT cell, so a
            // parked node inside a map branch keeps its branch identity. Mirrors InfraPark.
            Payload = marker,
            // The ladder's delay carries jitter, so it is computed ONCE here — never twice for the same park.
            DeadlineAt = now + DelayFor(state.Parks),
            TimeoutPayload = marker,
        });
    }

    /// <summary>
    /// Fold the prior park state (when it carries the <see cref="MarkerField"/>) + <paramref name="now"/> into the
    /// NEXT park: parks = prior + 1, the window anchored at the FIRST park. <see cref="State.WindowExhausted"/> is
    /// true when the whole <see cref="MaxParkWindow"/> has already elapsed.
    /// </summary>
    internal static State Next(JsonElement? priorState, DateTimeOffset now)
    {
        var prior = Read(priorState);
        var first = prior?.FirstParkedAtUtc ?? now;

        return new State
        {
            Parks = (prior?.Parks ?? 0) + 1,
            FirstParkedAtUtc = first,
            WindowExhausted = now - first >= MaxParkWindow,
        };
    }

    /// <summary>The wait before the <paramref name="parks"/>-th wake: the ladder rung (clamped to the last) with ±20% jitter. Zero/negative parks read the first rung (defensive).</summary>
    internal static TimeSpan DelayFor(int parks)
    {
        var rung = Schedule[Math.Clamp(parks - 1, 0, Schedule.Length - 1)];

        return rung * (0.8 + Random.Shared.NextDouble() * 0.4);
    }

    /// <summary>
    /// The park marker — BOTH the suspend payload (what the run detail shows while parked) and the wait's
    /// <c>TimeoutPayload</c> (what the deadline injects as the wake's resume payload). It names WHO must link WHAT
    /// so the parked run is actionable rather than merely alive, and merges OVER <paramref name="interrupted"/>
    /// (the prior marker, or on a first park the payload the node was resumed with) so the park never discards the
    /// state it interrupted — a from-node rerun's <c>approved: true</c> rides forward on it.
    /// </summary>
    internal static JsonElement Marker(JsonElement? interrupted, State state, ActorIdentityRequiredException fault)
    {
        var fields = new Dictionary<string, object?>();

        if (interrupted is { ValueKind: JsonValueKind.Object } carried)
            foreach (var property in carried.EnumerateObject())
                fields[property.Name] = property.Value.Clone();

        fields[MarkerField] = true;
        fields["parks"] = state.Parks;
        fields["firstParkedAtUtc"] = state.FirstParkedAtUtc.ToString("o");
        fields["actorUserId"] = fault.ActorUserId;
        fields["provider"] = fault.ProviderKind.ToString();
        fields["providerInstanceId"] = fault.ProviderInstanceId;
        fields["prompt"] = ParkedPrompt(fault);

        return JsonSerializer.SerializeToElement(fields);
    }

    /// <summary>
    /// What the run detail says while parked: the action needed, and the fact that nobody has to come back and press
    /// anything. Named <c>prompt</c> deliberately — that is the ONE payload key the bounded pending-wait read seam
    /// (<c>WorkflowRunPendingWaitObservationReader</c>) extracts in SQL, so this reaches the run detail through the
    /// existing contract with no widening of what crosses that seam. Any other key would be invisible there.
    /// </summary>
    private static string ParkedPrompt(ActorIdentityRequiredException fault) =>
        $"This step acts as one person's own {fault.ProviderKind} identity, and they haven't connected one yet. It resumes on its own within minutes of them connecting it (Connect remote → Personal), and gives up after {MaxParkWindow.TotalHours:0}h.";

    /// <summary>The honest ending: the window is spent, so say what was waited for and for how long rather than re-throwing the seam's "then retry" prompt at nobody.</summary>
    private static string ExhaustedMessage(ActorIdentityRequiredException fault) =>
        $"Waited {MaxParkWindow.TotalHours:0}h for this step's actor to connect their {fault.ProviderKind} identity and it never arrived. Ask them to connect {fault.ProviderKind} (Connect remote → Personal), then re-run this step.";

    /// <summary>Read a prior park's state — null when absent or not THIS park's marker, so a ladder only ever continues from its own parks.</summary>
    private static State? Read(JsonElement? priorState)
    {
        if (priorState is not { ValueKind: JsonValueKind.Object } payload) return null;

        if (!payload.TryGetProperty(MarkerField, out var marker) || marker.ValueKind != JsonValueKind.True) return null;

        var parks = payload.TryGetProperty("parks", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
        var first = payload.TryGetProperty("firstParkedAtUtc", out var f) && f.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(f.GetString(), out var parsed) ? parsed : (DateTimeOffset?)null;

        return first is null ? null : new State { Parks = parks, FirstParkedAtUtc = first.Value, WindowExhausted = false };
    }

    /// <summary>One park's durable position on the ladder (a data noun local to this concern).</summary>
    internal sealed record State
    {
        /// <summary>How many parks this wait has taken INCLUDING the one being staged (1-based).</summary>
        public required int Parks { get; init; }

        /// <summary>When the FIRST park of this wait was staged — the anchor <see cref="MaxParkWindow"/> is measured from.</summary>
        public required DateTimeOffset FirstParkedAtUtc { get; init; }

        /// <summary>True when the whole park window has elapsed — fail honestly instead of parking again.</summary>
        public required bool WindowExhausted { get; init; }
    }
}
