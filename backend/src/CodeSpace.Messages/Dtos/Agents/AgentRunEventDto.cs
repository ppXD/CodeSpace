using CodeSpace.Messages.Agents;

namespace CodeSpace.Messages.Dtos.Agents;

/// <summary>
/// One step in an agent run's append-only live log — what the agent did, in order. <see cref="Sequence"/>
/// is the monotonic cursor the UI passes back (as <c>after</c>) to fetch only newer steps, so the timeline
/// streams incrementally. Already redacted at the source. <see cref="Data"/> is the raw structured payload
/// as a JSON string (tool args, file paths, …), null when the event carries only text OR when the payload was
/// large enough to offload — then <see cref="DataArtifactId"/> is set and the consumer fetches the full payload
/// on demand via <c>GET /api/artifacts/{id}</c>.
/// </summary>
public sealed record AgentRunEventDto
{
    public required long Sequence { get; init; }
    public required AgentEventKind Kind { get; init; }
    public required string Text { get; init; }
    public string? Data { get; init; }

    /// <summary>D2 #1 — set when the structured payload was offloaded; fetch the full JSON from the artifact store. Null when <see cref="Data"/> is inline (small) or absent.</summary>
    public Guid? DataArtifactId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }
}
