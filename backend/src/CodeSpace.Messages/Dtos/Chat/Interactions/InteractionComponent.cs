using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Dtos.Chat.Interactions;

/// <summary>
/// What an interactive message RENDERS — a polymorphic component discriminated by <c>kind</c>.
/// Today's kinds are <see cref="ActionButtonsComponent"/> (a row of buttons) and
/// <see cref="FormComponent"/> (input fields the responder fills in chat); future kinds (a poll, or a
/// <c>composite</c> that holds a <c>Children</c> array for arbitrary combined layouts) slot in as
/// another <see cref="JsonDerivedTypeAttribute"/> — a new json <c>kind</c>, with ZERO migration (the
/// document lives in the <c>message.interaction_json</c> jsonb column).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ActionButtonsComponent), "action_buttons")]
[JsonDerivedType(typeof(FormComponent), "form")]
public abstract record InteractionComponent;

/// <summary>A row of clickable buttons. Each button's <see cref="InteractionButton.Key"/> is opaque to the framework — the workflow author decides what "approve" / "snooze" mean.</summary>
public sealed record ActionButtonsComponent : InteractionComponent
{
    public required IReadOnlyList<InteractionButton> Buttons { get; init; }
}

/// <summary>
/// A form the responder fills in chat — input fields whose submitted VALUES are injected into the
/// parked workflow run (surfaced as the wait node's <c>outputs.values</c>). <see cref="Fields"/> is a
/// JSON Schema (the same shape the schema-driven form renders), so the workflow author decides the
/// fields — a single "pick one of these" select is just a one-field form. Generic; no per-use-case
/// hardcoding. The submit control's response key is <see cref="MessageInteractionPolicy.FormSubmitKey"/>.
/// </summary>
public sealed record FormComponent : InteractionComponent
{
    /// <summary>JSON Schema describing the fields (rendered by the client's schema-driven form). Opaque server-side except for reading <c>required</c>.</summary>
    public required JsonElement Fields { get; init; }

    public string SubmitLabel { get; init; } = "Submit";
}

/// <summary>One button. <see cref="Key"/> is the response key surfaced back to the workflow; <see cref="Label"/> is the display text.</summary>
public sealed record InteractionButton
{
    public required string Key { get; init; }
    public required string Label { get; init; }

    /// <summary>Optional author-written note of what this button does — surfaced to the responder (tooltip) so a click's effect isn't opaque.</summary>
    public string? Description { get; init; }

    public InteractionButtonStyle Style { get; init; } = InteractionButtonStyle.Default;

    /// <summary>When true the UI must collect a comment before submitting this button (e.g. "request changes" wants a reason).</summary>
    public bool RequiresComment { get; init; }

    /// <summary>
    /// Whether clicking this button participates in RESOLVING the wait (the default). False = a
    /// non-terminal action: it's recorded in the log but never resolves — the card stays Open for others
    /// (e.g. an "ack" / "I'm looking" button alongside the real decision). Defaults true so existing
    /// buttons resolve exactly as before.
    /// </summary>
    public bool ResolvesWait { get; init; } = true;

    /// <summary>
    /// When true, clicking this button resolves the wait IMMEDIATELY regardless of the resolve policy — a
    /// short-circuit veto (e.g. one "request changes" blocks even under a 2-approval quorum). Only
    /// meaningful for a terminal (<see cref="ResolvesWait"/>) button.
    /// </summary>
    public bool Vetoes { get; init; }
}

/// <summary>Visual emphasis for a button. Serialized as a string so the wire value is stable + readable.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InteractionButtonStyle
{
    Default,
    Primary,
    Danger,
}
