using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Dtos.Sessions.Room;

/// <summary>
/// WHO produced one delivered artifact and under what conditions (Launch Extremis P21-8b) — the producing agent's own
/// execution status, log completeness, confinement posture and priced spend, attached to the
/// <see cref="DeliverableFile"/> or <see cref="DeliveryBlock"/> it made.
///
/// <para>It exists because an artifact's card could already say WHAT was delivered and whether a check graded it, but
/// never who made it, whether that agent had even finished, whether the host confined it, or what it cost. A file
/// listed under a still-Running producer read exactly like one listed under a Succeeded producer, in every
/// <c>WorkflowRunStatus</c>.</para>
///
/// <para>Every field is a recorded fact or an explicit absence: an unpriced model and one that captured no tokens
/// are both <c>null</c> cost (never <c>0</c>, which reads as free), a producer with no stream is
/// <c>null</c> <see cref="Logs"/> (never "settled"),
/// and an unrecorded posture is <see cref="RoomConfinementPosture.Unknown"/> (never
/// <see cref="RoomConfinementPosture.Confined"/>, which reads as safe). This projection never fabricates a value
/// that reads better than the evidence behind it.</para>
/// </summary>
public sealed record RoomArtifactProducer
{
    /// <summary>The agent run that produced this artifact — the terminal deep-link, and the id every other per-agent surface keys on.</summary>
    public required Guid AgentRunId { get; init; }

    /// <summary>
    /// The producer's lifecycle status as a stable string (the <c>AgentRunStatus</c> name) read straight off its own
    /// row — the same open vocabulary <see cref="RoomAgentCard.Status"/> speaks, from the same column.
    ///
    /// <para>The same vocabulary, NOT necessarily the same instant: the card's word arrives through the phase
    /// projection and this one through the projector's own agent-row read, so a status that lands between the two
    /// reads shows here first. On a COLLAPSED terminal turn that is by construction — the cached flow is re-attached
    /// to a fresh row read (a reaper terminalizes exactly that population after the parent finishes) while the
    /// cached card is not, which makes this field the more current of the two rather than an agreeing one.</para>
    /// </summary>
    public required string Status { get; init; }

    /// <summary>The producer's durable log-stream health. Null when it declared no stream at all — an unsaid fact, never a claim that its logs settled.</summary>
    public RoomAgentLogStatus? Logs { get; init; }

    /// <summary>What the host actually did to confine this producer — <see cref="RoomConfinementPosture.Unknown"/> (the default) whenever nothing recorded it.</summary>
    public RoomConfinementPosture Confinement { get; init; }

    /// <summary>This producer's REALIZED priced spend in USD (model price × its own captured tokens). Null when the model is unpriced, and null when its captured token counts are both zero — a zero-token row on a PRICED model prices to exactly <c>0</c>, which renders as "free" rather than as the "nothing landed yet" it actually records. A <c>0</c> that reaches here is therefore a priced-at-zero agent that really did consume tokens.</summary>
    public decimal? CostUsd { get; init; }
}

/// <summary>
/// What the sandbox ACTUALLY did to ONE producer, folded from that agent's own <c>sandbox_confinement</c> record by
/// the same ranking the run-level posture sentence uses — never a second definition of what a record means.
///
/// <para>Per artifact rather than per run because a turn's agents land on different workers: one file can come from a
/// confined producer and its sibling from an unconfined one, and the run's single least-confined sentence answers for
/// neither of them. That sentence stays exactly as it is; this rides beside it.</para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RoomConfinementPosture
{
    /// <summary>Nothing recorded a posture for this producer — an agent launched before the column existed, or a row that will not parse. The WEAKER claim by construction: an absent record must never render as a confinement nobody evidenced.</summary>
    Unknown = 0,

    /// <summary>The record says the command ran bare — either the host could not confine it, or the runner confines nothing. Any permission the tier expressed as "off" was NOT enforced by the OS.</summary>
    Unconfined,

    /// <summary>Confined (namespaces + a read-only root), but WITHOUT a severed network namespace.</summary>
    Confined,

    /// <summary>Confined AND handed a fresh empty net namespace — the strongest posture a producer can record.</summary>
    ConfinedNetworkSevered,
}
