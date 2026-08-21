using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Agents;

/// <summary>
/// What one harness adapter's stable native protocol can say about physical model calls. This describes observation
/// granularity only: it is not a claim that capture succeeded, nor that provider request/response bodies, timing, or
/// every provider-side subcall are available.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HarnessModelCallObservationCoverage
{
    /// <summary>No declaration was snapshotted. Used for legacy adapters/rows; never interpreted as no telemetry.</summary>
    LegacyUnknown,

    /// <summary>Each native response frame states a stable response id, effective model and per-response token usage. It does not promise provider wire bodies or request telemetry.</summary>
    PerResponseMetadata,

    /// <summary>The native stream states only a cumulative run/turn usage total and does not enumerate the calls that contributed to it.</summary>
    CumulativeAggregate,

    /// <summary>The adapter explicitly declares that its stable native stream exposes neither per-response metadata nor a cumulative model-usage aggregate.</summary>
    Unavailable,
}
