using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Agents;

/// <summary>
/// A capture promise's lifecycle (P2 durable capture, slice 1). <see cref="Committed"/> is the ONLY state that
/// asserts the capture sequence persisted its facts — and it covers the CONFIRMED-empty capture, which is a fact,
/// not an absence. <see cref="Indeterminate"/> is honest-unknown: the attempt died inside the capture window, so
/// the work may or may not exist — a recovery pass marks it, never silence. A later CONFIRMED observation may
/// supersede an Indeterminate (slice 2); it is never resurrected to Intended.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CaptureIntentStatus
{
    /// <summary>The harness exited and the capture sequence is in flight — the promise is open.</summary>
    Intended,

    /// <summary>The capture sequence ran to its persist; <c>FactsJson</c> records what landed (including an explicit empty).</summary>
    Committed,

    /// <summary>The attempt died inside the capture window — the side effects may or may not have run (outcome unknown). Marked by recovery, visible forever.</summary>
    Indeterminate,
}
