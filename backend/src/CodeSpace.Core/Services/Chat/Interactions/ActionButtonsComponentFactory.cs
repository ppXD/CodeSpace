using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Dtos.Chat.Interactions;

namespace CodeSpace.Core.Services.Chat.Interactions;

/// <summary>
/// Builds an <see cref="ActionButtonsComponent"/> from <c>{ "kind": "action_buttons", "buttons": [ … ] }</c>.
/// Each button carries the resolve flags (<see cref="InteractionButton.ResolvesWait"/> default true,
/// <see cref="InteractionButton.Vetoes"/>) so quorum / non-terminal-discussion is authorable per button.
/// Returns null if no valid button — so an empty list posts a plain message, not a dead card.
/// </summary>
public sealed class ActionButtonsComponentFactory : IInteractionComponentFactory, ISingletonDependency
{
    public string Kind => "action_buttons";

    public InteractionComponent? Build(JsonElement config)
    {
        if (!config.TryGetProperty("buttons", out var buttonsEl) || buttonsEl.ValueKind != JsonValueKind.Array) return null;

        var buttons = new List<InteractionButton>();

        foreach (var b in buttonsEl.EnumerateArray())
        {
            if (b.ValueKind != JsonValueKind.Object) continue;

            var key = ReadString(b, "key");
            var label = ReadString(b, "label");
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(label)) continue;

            buttons.Add(new InteractionButton
            {
                Key = key,
                Label = label,
                Description = ReadString(b, "description"),
                Style = ReadStyle(b),
                RequiresComment = ReadBool(b, "requiresComment", false),
                ResolvesWait = ReadBool(b, "resolvesWait", true),   // default: a button is a terminal decision
                Vetoes = ReadBool(b, "vetoes", false),
            });
        }

        return buttons.Count > 0 ? new ActionButtonsComponent { Buttons = buttons } : null;
    }

    private static string? ReadString(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool ReadBool(JsonElement obj, string key, bool fallback) =>
        obj.TryGetProperty(key, out var v) ? v.ValueKind == JsonValueKind.True : fallback;

    private static InteractionButtonStyle ReadStyle(JsonElement obj) =>
        obj.TryGetProperty("style", out var s) && s.ValueKind == JsonValueKind.String && Enum.TryParse<InteractionButtonStyle>(s.GetString(), ignoreCase: true, out var parsed)
            ? parsed : InteractionButtonStyle.Default;
}
