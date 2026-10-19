using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Credentials;

/// <summary>
/// Begins a provider-credential OAuth flow. Returns the authorize URL the SPA should open
/// in a popup or full redirect. The provider's callback hits <c>CompleteCredentialOAuthCommand</c>.
///
/// <para><see cref="IRequireCredentialReauthorization"/>: a connect that names no credential mints one and takes
/// <c>credentials.manage</c>; one that names <see cref="ReplaceCredentialId"/> re-authorizes it in place, which its own person may do on
/// membership alone — anyone else needs <c>credentials.manage</c>.</para>
/// </summary>
public sealed record InitCredentialOAuthCommand : ICommand<InitCredentialOAuthResult>, IRequireCredentialReauthorization
{
    /// <summary>Provider instance we're authorising against. Must belong to the current team.</summary>
    public required Guid ProviderInstanceId { get; init; }

    /// <summary>Human-friendly name for the resulting credential row (e.g. "Maya's GitLab OAuth").</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// User who will own the resulting credential. NULL = team-shared (only Admins can set
    /// this). Defaults to the initiating user (set by the handler if absent).
    /// </summary>
    public Guid? IntendedOwnerUserId { get; init; }

    /// <summary>
    /// Where to redirect the browser after success, with query params appended. Must be a path on this site
    /// (a single leading '/') or an address on an origin this deployment serves its web app from
    /// (<c>OAuthReturnOriginsSetting</c>); anything else is refused here with 400 <c>invalid_request</c>, before a
    /// flow starts, and checked again at the callback. The SPA names its own origin first and asks again with a path
    /// on that refusal.
    /// </summary>
    public string? ReturnUrl { get; init; }

    /// <summary>Override the provider_instance's default scope list. Usually null.</summary>
    public IReadOnlyList<string>? Scopes { get; init; }

    /// <summary>
    /// The credential this connect re-authorizes IN PLACE instead of creating another: when the callback completes, the new
    /// token is written into this row — same id, so what is bound through it needs no moving — and what its dead token had
    /// turned Error comes back. Null (what a client that predates this field sends) connects a new credential exactly as
    /// before. It must be a Personal credential of this team and provider instance, owned by the person; the sign-in must
    /// be the same provider account the credential was connected as, or the callback is refused with
    /// <c>credential_account_mismatch</c>. A pod from before this field ignores it and creates a new credential, as a
    /// connect always did. Who may name one: its owner, or whoever holds <c>credentials.manage</c> (and reaches the owner by rank).
    /// </summary>
    public Guid? ReplaceCredentialId { get; init; }
}

public sealed record InitCredentialOAuthResult
{
    public required Uri AuthorizeUrl { get; init; }
    public required string State { get; init; }

    /// <summary>The nonce that binds this flow to the starting browser. The controller sets it as an HttpOnly cookie and never returns it in a body.</summary>
    public required string BrowserNonce { get; init; }

    /// <summary>How long the flow stays completable — the binding cookie expires with it.</summary>
    public required TimeSpan Lifetime { get; init; }

    /// <summary>
    /// The path the binding cookie is scoped to: the callback's own, as the browser is sent to it (<c>OAuth:CallbackUrl</c>,
    /// so it holds behind a path-rewriting proxy). Carried here so the controller needs nothing but the mediator.
    /// </summary>
    public required string BrowserNonceCookiePath { get; init; }
}
