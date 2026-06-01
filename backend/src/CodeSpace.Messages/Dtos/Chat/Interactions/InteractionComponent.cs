using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Dtos.Chat.Interactions;

/// <summary>
/// What an interactive message RENDERS — a polymorphic component discriminated by <c>kind</c>.
/// Today the only kind is <see cref="ActionButtonsComponent"/> (a row of buttons); future kinds
/// (a form, a poll, or a <c>composite</c> that holds a <c>Children</c> array for arbitrary combined
/// layouts) slot in as another <see cref="JsonDerivedTypeAttribute"/> — a new json <c>kind</c>, with
/// ZERO migration (the document lives in the <c>message.interaction_json</c> jsonb column).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ActionButtonsComponent), "action_buttons")]
public abstract record InteractionComponent;

/// <summary>A row of clickable buttons. Each button's <see cref="InteractionButton.Key"/> is opaque to the framework — the workflow author decides what "approve" / "snooze" mean.</summary>
public sealed record ActionButtonsComponent : InteractionComponent
{
    public required IReadOnlyList<InteractionButton> Buttons { get; init; }
}

/// <summary>One button. <see cref="Key"/> is the response key surfaced back to the workflow; <see cref="Label"/> is the display text.</summary>
public sealed record InteractionButton
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public InteractionButtonStyle Style { get; init; } = InteractionButtonStyle.Default;

    /// <summary>When true the UI must collect a comment before submitting this button (e.g. "request changes" wants a reason).</summary>
    public bool RequiresComment { get; init; }
}

/// <summary>Visual emphasis for a button. Serialized as a string so the wire value is stable + readable.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InteractionButtonStyle
{
    Default,
    Primary,
    Danger,
}
