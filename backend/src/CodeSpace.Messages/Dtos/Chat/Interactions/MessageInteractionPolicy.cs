namespace CodeSpace.Messages.Dtos.Chat.Interactions;

/// <summary>
/// Pure rules for responding to an interactive message — shared by the respond endpoint's service so
/// they're unit-testable in isolation and consistent. Generic over the component kind: a new
/// component (form, poll, …) extends <see cref="IsValidResponse"/>'s switch with its own option set.
/// </summary>
public static class MessageInteractionPolicy
{
    /// <summary>True if <paramref name="responseKey"/> is a selectable option of the interaction's component (e.g. a button key).</summary>
    public static bool IsValidResponse(MessageInteraction interaction, string responseKey) =>
        interaction.Component switch
        {
            ActionButtonsComponent buttons => buttons.Buttons.Any(b => b.Key == responseKey),
            _ => false,
        };

    /// <summary>
    /// True if <paramref name="userId"/> may respond: they must be an active member of the conversation
    /// AND, when the interaction restricts responders, be in that set (null = any conversation member).
    /// </summary>
    public static bool IsAllowedResponder(MessageInteraction interaction, Guid userId, bool isConversationMember) =>
        isConversationMember && (interaction.AllowedResponderUserIds is null || interaction.AllowedResponderUserIds.Contains(userId));
}
