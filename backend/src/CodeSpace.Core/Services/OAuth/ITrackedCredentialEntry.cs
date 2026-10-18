using CodeSpace.Core.Persistence.Entities;

namespace CodeSpace.Core.Services.OAuth;

/// <summary>
/// Tells the DbContext that tracks a credential entity, if any, that the payload columns the entity now holds are what the
/// row holds. A refresh stores its pair on a connection of its own, behind the DbContext, and then points the caller's
/// entity at it so later reads in the scope see the current token; to the DbContext that is an edit it has not saved. Its
/// next <c>SaveChanges</c> would write the entity's payload back — over a newer rotation another scope or pod stored since,
/// which leaves the row holding a refresh token the provider has already consumed.
///
/// <para>Only the context of the caller's own scope is reached. An entity tracked by a context of another scope keeps its
/// own view; the refresher is handed the entity of the scope it runs in.</para>
/// </summary>
public interface ITrackedCredentialEntry
{
    /// <summary>
    /// Takes the entity's current <c>EncryptedPayload</c>, <c>ExpiresDate</c>, <c>LastModifiedDate</c> and
    /// <c>LastModifiedBy</c> — the columns a payload write sets — as what the database holds, so a <c>SaveChanges</c>
    /// writes none of them. Every other change the entity carries stays pending. No-op for an entity no context tracks.
    /// </summary>
    void MarkPayloadStored(Credential credential);
}
