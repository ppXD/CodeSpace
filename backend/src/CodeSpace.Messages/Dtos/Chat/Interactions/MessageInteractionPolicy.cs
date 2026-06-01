using System.Text.Json;

namespace CodeSpace.Messages.Dtos.Chat.Interactions;

/// <summary>
/// Pure rules for responding to an interactive message — shared by the respond endpoint's service so
/// they're unit-testable in isolation and consistent. Generic over the component kind: a new
/// component (poll, …) extends <see cref="IsValidResponse"/>'s switch with its own option set.
/// </summary>
public static class MessageInteractionPolicy
{
    /// <summary>The single response key a <see cref="FormComponent"/> accepts — its submit control.</summary>
    public const string FormSubmitKey = "submit";

    /// <summary>True if <paramref name="responseKey"/> is a selectable option of the interaction's component (a button key, or a form's submit).</summary>
    public static bool IsValidResponse(MessageInteraction interaction, string responseKey) =>
        interaction.Component switch
        {
            ActionButtonsComponent buttons => buttons.Buttons.Any(b => b.Key == responseKey),
            FormComponent => responseKey == FormSubmitKey,
            _ => false,
        };

    /// <summary>
    /// For a <see cref="FormComponent"/>, the names of <c>required</c> fields (per the form's JSON
    /// Schema) that are absent or empty in <paramref name="values"/>. Empty for any other component or
    /// when all required fields are supplied — the server enforces this, not just the UI.
    /// </summary>
    public static IReadOnlyList<string> MissingRequiredFields(MessageInteraction interaction, IReadOnlyDictionary<string, JsonElement>? values)
    {
        if (interaction.Component is not FormComponent form) return [];
        if (form.Fields.ValueKind != JsonValueKind.Object || !form.Fields.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.Array) return [];

        var missing = new List<string>();
        foreach (var nameEl in required.EnumerateArray())
        {
            if (nameEl.ValueKind != JsonValueKind.String) continue;

            var name = nameEl.GetString()!;
            if (values == null || !values.TryGetValue(name, out var v) || IsEmptyValue(v)) missing.Add(name);
        }

        return missing;
    }

    private static bool IsEmptyValue(JsonElement v) =>
        v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
        || (v.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(v.GetString()))
        || (v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 0);

    /// <summary>
    /// True if <paramref name="userId"/> may respond: they must be an active member of the conversation
    /// AND, when the interaction restricts responders, be in that set (null = any conversation member).
    /// </summary>
    public static bool IsAllowedResponder(MessageInteraction interaction, Guid userId, bool isConversationMember) =>
        isConversationMember && (interaction.AllowedResponderUserIds is null || interaction.AllowedResponderUserIds.Contains(userId));

    /// <summary>
    /// True if the chosen option mandates a comment (e.g. a "request changes" button with
    /// <see cref="InteractionButton.RequiresComment"/>). The server enforces this — it isn't a UI-only hint.
    /// </summary>
    public static bool RequiresComment(MessageInteraction interaction, string responseKey) =>
        interaction.Component is ActionButtonsComponent buttons
        && buttons.Buttons.FirstOrDefault(b => b.Key == responseKey) is { RequiresComment: true };
}
