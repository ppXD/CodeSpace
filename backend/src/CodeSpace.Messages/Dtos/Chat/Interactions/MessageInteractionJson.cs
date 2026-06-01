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
}
