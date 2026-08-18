using System.Text.Json.Serialization;
using CodeSpace.Messages.Contracts;

namespace CodeSpace.Messages.Agents;

/// <summary>
/// HOW WELL a projection represents what the harness actually said. It is load-bearing, not descriptive: a fact
/// scraped out of prose and a fact read from a structured telemetry frame are indistinguishable once both are a
/// normalized event, and treating the first as the second is how a guessed cost gets billed and a guessed tool call
/// gets audited. Only <see cref="Exact"/> and <see cref="RedactedExact"/> may back a strict read; everything else
/// stays visible and stays qualified.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SemanticProjectionQuality
{
    /// <summary>The harness stated this, and the projection carries it unchanged.</summary>
    Exact,

    /// <summary>The harness stated this; secret spans were masked, and nothing else was altered.</summary>
    RedactedExact,

    /// <summary>Computed from stated facts by a total, documented rule (a sum, a difference, a lookup) — correct if its inputs are, and never more certain than they are.</summary>
    Derived,

    /// <summary>Inferred from unstructured output by pattern matching. A best effort that may be wrong, and must never be presented as what the harness said.</summary>
    Heuristic,

    /// <summary>Provenance is not established. The event is retained so it stays visible, and no strict read may rest on it.</summary>
    Unknown,
}

/// <summary>Extension for the one boundary every strict reader asks about.</summary>
public static class SemanticProjectionQualityExtensions
{
    /// <summary>Whether this quality may back a strict read — the projection IS what the harness said, modulo masking.</summary>
    public static bool IsExactlyGrounded(this SemanticProjectionQuality value) => value is SemanticProjectionQuality.Exact or SemanticProjectionQuality.RedactedExact;
}

/// <summary>Whether a consumer must handle this event or may skip it. Stated by the PRODUCER, so a new event type an old reader cannot route is either an explicit obligation or an explicit safe-to-ignore, never a guess.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SemanticEventNecessity
{
    /// <summary>A reader that cannot route this event must fail loudly rather than continue with an incomplete picture.</summary>
    Required,

    /// <summary>A reader that cannot route this event may skip it without losing a fact it is accountable for.</summary>
    Ignorable,
}

/// <summary>
/// The PROJECTION of one or more native records into the normalized vocabulary the rest of the system reads.
/// Persisted as <c>workflow_run_semantic_event</c>. It never replaces its sources: <see cref="SourceNativeRecordIds"/>
/// keeps the exact frames it was folded from, and <see cref="ProjectionQuality"/> keeps how faithfully — so a later
/// reader can always ask "did the harness say this, or did we work it out?" and get a truthful answer.
/// </summary>
public sealed record AgentSemanticEventV1
{
    /// <summary>Data-contract version these fields are stamped with.</summary>
    public required int ContractVersion { get; init; }

    /// <summary>Identity of this event, which <see cref="CausationId"/> on a later event points back at.</summary>
    public required Guid EventId { get; init; }

    /// <summary>Absolute URI naming what happened. A URI rather than a bare word so a harness-specific or operator-defined event cannot collide with a first-party one.</summary>
    public required string EventType { get; init; }

    /// <summary>Version of the payload schema for this <see cref="EventType"/>, one-based. Independent of <see cref="ContractVersion"/>: an event type evolves without reversioning the whole plane.</summary>
    public required int EventSchemaVersion { get; init; }

    /// <summary>The native records this event was folded from, in source order. Empty is only honest for a quality that is not exactly grounded — an "exact" fact with no frame behind it is a claim about nothing.</summary>
    public required IReadOnlyList<Guid> SourceNativeRecordIds { get; init; }

    /// <summary>The harness execution this event belongs to.</summary>
    public required Guid ExecutionId { get; init; }

    /// <summary>The harness session, when the execution ran inside one.</summary>
    public Guid? SessionId { get; init; }

    /// <summary>The turn within the session, when the harness delimits turns.</summary>
    public Guid? TurnId { get; init; }

    /// <summary>The step within the turn, when the harness delimits steps.</summary>
    public Guid? StepId { get; init; }

    /// <summary>The model call this event concerns, when it concerns one — the join that lets an exact-telemetry event carry cost.</summary>
    public Guid? ModelCallId { get; init; }

    /// <summary>The tool call this event concerns, when it concerns one.</summary>
    public Guid? ToolCallId { get; init; }

    /// <summary>Groups every event belonging to one logical activity, across executions.</summary>
    public Guid? CorrelationId { get; init; }

    /// <summary>The event that directly caused this one, when there is one.</summary>
    public Guid? CausationId { get; init; }

    /// <summary>Whether a reader that cannot route this event may skip it.</summary>
    public required SemanticEventNecessity Necessity { get; init; }

    /// <summary>How faithfully this event represents its sources. Required with no default, so a projection must STATE its fidelity rather than inherit the most flattering one.</summary>
    public required SemanticProjectionQuality ProjectionQuality { get; init; }

    /// <summary>The projected payload in operator-configured storage, when it is too large to ride with the event.</summary>
    public WorkflowRunArtifactRefV1? PayloadRef { get; init; }

    /// <summary>Every reason this event cannot be trusted as a projection. Empty ⇒ readable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (!WorkflowRunDataContract.IsSupported(ContractVersion))
            errors.Add($"contractVersion '{ContractVersion}' is unsupported");
        if (EventId == Guid.Empty)
            errors.Add("eventId must be non-empty");
        if (!Uri.TryCreate(EventType, UriKind.Absolute, out _))
            errors.Add($"eventType '{EventType}' must be an absolute URI");
        if (EventSchemaVersion <= 0)
            errors.Add("eventSchemaVersion must be one-based");
        if (ExecutionId == Guid.Empty)
            errors.Add("executionId must be non-empty");
        if (!Enum.IsDefined(Necessity))
            errors.Add($"necessity '{Necessity}' is unsupported");

        errors.AddRange(GroundingErrors());

        return errors;
    }

    /// <summary>
    /// Value equality that compares <see cref="SourceNativeRecordIds"/> ELEMENT-WISE. The generated record equality
    /// compares it through <c>EqualityComparer&lt;IReadOnlyList&lt;Guid&gt;&gt;.Default</c>, i.e. by reference, so
    /// two identically grounded events — including an event and its own round trip — never compare equal.
    /// </summary>
    public bool Equals(AgentSemanticEventV1? other) =>
        other is not null
        && ContractVersion == other.ContractVersion
        && EventId == other.EventId
        && string.Equals(EventType, other.EventType, StringComparison.Ordinal)
        && EventSchemaVersion == other.EventSchemaVersion
        && SourceNativeRecordIds.SequenceEqual(other.SourceNativeRecordIds)
        && ExecutionId == other.ExecutionId
        && SessionId == other.SessionId
        && TurnId == other.TurnId
        && StepId == other.StepId
        && ModelCallId == other.ModelCallId
        && ToolCallId == other.ToolCallId
        && CorrelationId == other.CorrelationId
        && CausationId == other.CausationId
        && Necessity == other.Necessity
        && ProjectionQuality == other.ProjectionQuality
        && PayloadRef == other.PayloadRef;

    /// <summary>Hashes a subset of what <see cref="Equals(AgentSemanticEventV1)"/> compares — every field here is also compared, and equal source lists have equal counts, which is the whole contract a hash owes.</summary>
    public override int GetHashCode() => HashCode.Combine(ContractVersion, EventId, EventType, EventSchemaVersion, ExecutionId, Necessity, ProjectionQuality, SourceNativeRecordIds.Count);

    private IEnumerable<string> GroundingErrors()
    {
        if (SourceNativeRecordIds.Any(id => id == Guid.Empty))
            yield return "sourceNativeRecordIds must not contain an empty id";
        if (!Enum.IsDefined(ProjectionQuality))
            yield return $"projectionQuality '{ProjectionQuality}' is unsupported";
        if (ProjectionQuality.IsExactlyGrounded() && SourceNativeRecordIds.Count == 0)
            yield return "projectionQuality claims exactness but cites no source native record";

        if (PayloadRef is null) yield break;

        foreach (var error in PayloadRef.Validate()) yield return $"payloadRef: {error}";
    }
}
