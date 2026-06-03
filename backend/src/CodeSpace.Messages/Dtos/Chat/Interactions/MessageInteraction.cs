using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Dtos.Chat.Interactions;

/// <summary>
/// An interactive component attached to a chat message (the stored, server-side shape). Three
/// orthogonal parts: <see cref="Component"/> (what to render), <see cref="Target"/> (where a response
/// routes — server-side only), and the outcome (<see cref="State"/> + <see cref="Resolution"/>).
/// Serialized to the <c>message.interaction_json</c> jsonb column. <see cref="Version"/> lets the
/// schema evolve; new component / target kinds are additive (a new json <c>kind</c>, no migration).
///
/// <para>The workflow wait is the idempotency authority for resolution; <see cref="State"/> /
/// <see cref="Resolution"/> here are the display mirror stamped after a successful resolve.</para>
/// </summary>
public sealed record MessageInteraction
{
    public int Version { get; init; } = 1;

    public required InteractionComponent Component { get; init; }

    /// <summary>Where a response routes. SERVER-SIDE ONLY — carries the wait token; never surfaced to clients (see <see cref="MessageInteractionView"/>).</summary>
    public required InteractionTarget Target { get; init; }

    /// <summary>Who may respond. Null = any active member of the conversation; otherwise only these users (e.g. the picked reviewer).</summary>
    public IReadOnlyList<Guid>? AllowedResponderUserIds { get; init; }

    /// <summary>
    /// The append-only collaboration log — every comment and every action click, in order, so the card
    /// shows the full team timeline (who said / clicked what, when). Independent of <see cref="State"/>:
    /// a card with no terminating wait keeps accumulating responses while staying Open (a living thread).
    /// Empty for an untouched card AND for any pre-existing message (the field defaults empty on read).
    /// </summary>
    public IReadOnlyList<InteractionResponse> Responses { get; init; } = [];

    public InteractionState State { get; init; } = InteractionState.Open;

    /// <summary>Set when resolved — which response, by whom, when. Null while <see cref="State"/> is <see cref="InteractionState.Open"/>.</summary>
    public InteractionResolution? Resolution { get; init; }
}

/// <summary>
/// One entry in an interaction's append-only response log. A <see cref="InteractionResponseKind.Comment"/>
/// is non-terminal discussion (any conversation member may add, repeatedly); an
/// <see cref="InteractionResponseKind.Action"/> records a button click. The terminal action that resolves
/// the interaction is logged here too (mirrored from <see cref="InteractionResolution"/>) so the timeline is complete.
/// </summary>
public sealed record InteractionResponse
{
    public required Guid ByUserId { get; init; }

    public required InteractionResponseKind Kind { get; init; }

    /// <summary>The action key for an <see cref="InteractionResponseKind.Action"/> (e.g. "approve"); null for a comment.</summary>
    public string? Key { get; init; }

    public string? Comment { get; init; }

    public required DateTimeOffset AtUtc { get; init; }
}

/// <summary>Kind of a logged response. Serialized as a string for a stable, readable wire value.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InteractionResponseKind
{
    Action,
    Comment,
}

/// <summary>
/// The client-facing projection of a <see cref="MessageInteraction"/> — identical EXCEPT it omits
/// <see cref="MessageInteraction.Target"/>, so the wait token never leaves the server. The frontend
/// renders from <see cref="Component"/> and POSTs a response keyed by the message id (the backend
/// re-derives the target). Carried on <see cref="MessageView"/>; null for a plain message.
/// </summary>
public sealed record MessageInteractionView
{
    public required int Version { get; init; }
    public required InteractionComponent Component { get; init; }
    public IReadOnlyList<Guid>? AllowedResponderUserIds { get; init; }
    public required IReadOnlyList<InteractionResponse> Responses { get; init; }
    public required InteractionState State { get; init; }
    public InteractionResolution? Resolution { get; init; }
}

/// <summary>Lifecycle of an interaction. Serialized as a string for a stable, readable wire value.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InteractionState
{
    Open,
    Resolved,
    Expired,
}

/// <summary>The outcome of an interaction once a response lands. The display mirror of the resolved workflow wait.</summary>
public sealed record InteractionResolution
{
    /// <summary>Which response was chosen — a component option key (e.g. a button's <c>Key</c>).</summary>
    public required string ResponseKey { get; init; }

    /// <summary>The authenticated user who responded.</summary>
    public required Guid ByUserId { get; init; }

    public string? Comment { get; init; }

    /// <summary>For a form response — the submitted field values (mirrors the run's injected input). Null for a button response.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Values { get; init; }

    public required DateTimeOffset AtUtc { get; init; }
}
