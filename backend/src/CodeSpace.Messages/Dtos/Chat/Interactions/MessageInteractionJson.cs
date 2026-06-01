using System.Text.Json;

namespace CodeSpace.Messages.Dtos.Chat.Interactions;

/// <summary>
/// (De)serializes a <see cref="MessageInteraction"/> to/from the raw jsonb stored in
/// <c>message.interaction_json</c>. One shared options instance so the stored shape is stable;
/// polymorphism (component / target <c>kind</c> discriminators) and string enums ride the type
/// attributes, so this is just camelCase + web defaults. Null-tolerant: a null model / null column
/// round-trips to null (a plain message).
/// </summary>
public static class MessageInteractionJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string? Serialize(MessageInteraction? interaction) =>
        interaction is null ? null : JsonSerializer.Serialize(interaction, Options);

    public static MessageInteraction? Deserialize(string? json) =>
        string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<MessageInteraction>(json, Options);

    /// <summary>
    /// Tolerant variant for the READ/display path: returns null on malformed jsonb OR an unknown
    /// component/target <c>kind</c> (a card written by a newer or forked server) INSTEAD of throwing —
    /// so a single unrenderable card can't brick a whole message-list read. The strict
    /// <see cref="Deserialize"/> stays for paths that legitimately require a well-formed interaction.
    /// </summary>
    public static MessageInteraction? TryDeserialize(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<MessageInteraction>(json, Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // JsonException = malformed jsonb; NotSupportedException = unknown polymorphic `kind`.
            return null;
        }
    }
}
