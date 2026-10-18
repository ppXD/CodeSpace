using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Credentials;

/// <summary>
/// Puts a new token into a credential that already exists, in place: the credential keeps its id, so every repository, group
/// hook and act-as identity bound through it keeps working, and the ones its dead token had turned Error come back. For a
/// token that expired or was revoked at the provider, and for a replacement of an OAuth sign-in by a token — the row's auth
/// type follows the new token.
///
/// <para>The token is probed against the provider before anything is written, and must belong to the SAME provider account
/// the credential was connected as: another account is a new connection, not a re-authorization, and is refused with
/// <c>credential_account_mismatch</c>. A credential retired when its person left the team or its connection was removed is
/// refused with <c>credential_retired</c>.</para>
///
/// <para><see cref="IRequireCredentialManagement"/>: the owner of a Personal credential on membership alone; anyone else —
/// a teammate's or a team-service credential — needs <c>credentials.manage</c>.</para>
/// </summary>
public sealed record ReplaceCredentialTokenCommand : ICommand<ReplaceCredentialTokenResult>, IRequireCredentialManagement
{
    /// <summary>Set by the controller from the route segment via `command with { CredentialId = ... }`. Non-required so System.Text.Json doesn't 400-fail when the body omits it (URL is authoritative).</summary>
    public Guid CredentialId { get; init; }

    /// <summary>The new token. Carried in the request BODY only; never logged or echoed back.</summary>
    public required string Token { get; init; }
}

public sealed record ReplaceCredentialTokenResult
{
    public required Guid CredentialId { get; init; }

    /// <summary>The provider account the new token belongs to, when the provider could say — what the person can check it against.</summary>
    public string? ProviderUsername { get; init; }
}
