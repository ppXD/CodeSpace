using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Contracts;

/// <summary>How the run's execution ENDED (F0 dimension 1 of 5).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExecutionDisposition
{
    Completed,
    ForcedStop,
    Crashed,
    Cancelled,
}

/// <summary>Whether the run's OBJECTIVE was met (F0 dimension 2) — the M1a four-state vocabulary.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OutcomeDisposition
{
    Solved,
    Unsolved,
    Abstained,
    Unknown,
}

/// <summary>Whether the run's produced work was durably CAPTURED (F0 dimension 4).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ArtifactDisposition
{
    Captured,
    CaptureFailed,
    NothingExpected,
    Unknown,
}

/// <summary>Whether the run's delivery obligation was met (F0 dimension 5). <see cref="WaivedByPolicy"/> NEVER counts as delivered in any satisfied-metric.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeliveryDisposition
{
    Delivered,
    PolicyBlocked,
    WaivedByPolicy,
    NotRequired,
    Unknown,
}

/// <summary>
/// The FIVE-dimensional completion verdict (F0) — derived by the ONE reducer from already-recorded facts
/// (requirement + receipt envelopes + run facts), never authored ad hoc by a renderer. Every consumer
/// (scorecard, gates, Room, journal) reads THIS; the same contract + the same facts must produce the same
/// assessment on every mode (the v4.1-F exit assertion). A run whose assessment cannot be derived is
/// <c>Unknown</c>-dimensioned — post-CUTOVER, Unknown is never presented as Success.
/// </summary>
public sealed record CompletionAssessment
{
    /// <summary>What this assessment was derived from — see <see cref="CompletionBasis"/> for the CUTOVER semantics.</summary>
    public required CompletionBasis Basis { get; init; }

    public required ExecutionDisposition Execution { get; init; }

    /// <summary>The forced stop's recorded reason (<c>SupervisorStopReasons</c> vocabulary) — present exactly when <see cref="Execution"/> is <see cref="ExecutionDisposition.ForcedStop"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ForcedStopReason { get; init; }

    public required OutcomeDisposition Outcome { get; init; }

    public required VerificationDisposition Verification { get; init; }

    public required ArtifactDisposition Artifact { get; init; }

    public required DeliveryDisposition Delivery { get; init; }
}
