using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Credentials;

/// <summary>
/// Finishes a connect whose callback arrived without the starting browser's binding cookie — the browser could not keep
/// it (the SPA and the API on different sites), or the connect was started on another host than <c>OAuth:CallbackUrl</c>
/// names. The callback hands the code and state to the SPA's completion page, and the signed-in SPA posts them here.
///
/// <para>Bound to the person instead of the browser: only the caller who started the connect consumes its state. A
/// forwarded authorize link lands on the victim's own session, which is not the starter's, so it completes nothing and
/// consumes nothing — the same refusal the binding cookie gives.</para>
/// </summary>
public sealed record CompleteCredentialOAuthAsInitiatorCommand : ICommand<CompleteCredentialOAuthResult>, IRequireAuthenticatedUser
{
    public required string Code { get; init; }
    public required string State { get; init; }
}
