using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Enums;

/// <summary>
/// What a person should read off a credential, derived from what is recorded about it (never stored): whether it works,
/// is about to stop, was refused by its provider, was disconnected, or lent by someone no longer in the team. Serialized
/// as the lower-case names clients branch on, so a rename is a breaking change.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CredentialHealth>))]
public enum CredentialHealth
{
    /// <summary>Nothing recorded says it will not work.</summary>
    [JsonStringEnumMemberName("active")] Active,

    /// <summary>A static token whose provider-side expiry is inside the warning window (<c>DaysToExpiry</c> says how long is left).</summary>
    [JsonStringEnumMemberName("expiring")] Expiring,

    /// <summary>The provider refused it — a dead OAuth grant, a rejected or expired static token — and it needs reconnecting.</summary>
    [JsonStringEnumMemberName("rejected")] Rejected,

    /// <summary>Disconnected: its token material is gone.</summary>
    [JsonStringEnumMemberName("revoked")] Revoked,

    /// <summary>A person's credential whose person no longer has standing in the team; the retirement sweep is about to retire it.</summary>
    [JsonStringEnumMemberName("owner_left")] OwnerLeft
}
