using CodeSpace.Messages.Commands.Credentials;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CodeSpace.Api.Controllers;

/// <summary>
/// OAuth Authorization Code + PKCE flow for provider credentials.
///
/// <c>POST /init</c> is authenticated (the caller picks the target provider_instance).
/// <c>GET /callback</c> is anonymous — the provider's redirect can't carry our JWT. State
/// is the proof of identity: 32-byte CSPRNG, one-time use, 10-min TTL, PK-indexed.
/// </summary>
[ApiController]
[Route("api/credentials/oauth")]
public class CredentialOAuthController : ControllerBase
{
    private readonly IMediator _mediator;

    public CredentialOAuthController(IMediator mediator) { _mediator = mediator; }

    [HttpPost("init")]
    public async Task<IActionResult> Init([FromBody] InitCredentialOAuthCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return Ok(new { authorizeUrl = result.AuthorizeUrl.ToString(), state = result.State });
    }

    [HttpGet("callback")]
    [AllowAnonymous]
    public async Task<IActionResult> Callback([FromQuery] OAuthCallbackQuery query, CancellationToken cancellationToken)
    {
        // Provider rejected (e.g. user clicked deny). Bounce back to a generic UI page
        // with the provider's error code so the SPA can show a sensible message.
        if (!string.IsNullOrEmpty(query.Error)) return Redirect(BuildErrorReturn("/", query.Error, query.ErrorDescription));

        if (string.IsNullOrEmpty(query.Code) || string.IsNullOrEmpty(query.State)) return BadRequest(new { code = "oauth_callback_invalid", message = "missing code or state" });

        var result = await _mediator.Send(new CompleteCredentialOAuthCommand { Code = query.Code, State = query.State }, cancellationToken).ConfigureAwait(false);

        return Redirect(BuildSuccessReturn(result.ReturnUrl, result.CredentialId));
    }

    private static string BuildSuccessReturn(string returnUrl, Guid credentialId)
    {
        var sep = returnUrl.Contains('?') ? '&' : '?';
        return $"{returnUrl}{sep}oauthCredentialId={Uri.EscapeDataString(credentialId.ToString())}";
    }

    private static string BuildErrorReturn(string returnUrl, string error, string? description)
    {
        var sep = returnUrl.Contains('?') ? '&' : '?';
        var qs = $"oauthError={Uri.EscapeDataString(error)}";
        if (!string.IsNullOrEmpty(description)) qs += $"&oauthErrorDescription={Uri.EscapeDataString(description)}";
        return $"{returnUrl}{sep}{qs}";
    }

    /// <summary>
    /// Wire-binding shape for the callback's query string. Controller-local because it's a
    /// pure model-binding artefact — not an <c>ICommand</c>/<c>IQuery</c> and never round-trips
    /// through the mediator. The success path hand-maps <c>Code</c>+<c>State</c> into
    /// <see cref="CompleteCredentialOAuthCommand"/>; the error path consumes <c>Error</c>+
    /// <c>ErrorDescription</c> for the redirect and skips the mediator entirely.
    ///
    /// <para>CLAUDE.md Rule 17 sibling-DTO exception: this endpoint accepts two mutually-
    /// exclusive shapes (success vs denial), so the controller picks one. The Command record
    /// stays narrow (success-only). <c>error_description</c> is bound by explicit name because
    /// OAuth 2.0 mandates the snake_case wire format.</para>
    /// </summary>
    public sealed record OAuthCallbackQuery
    {
        public string? Code { get; init; }
        public string? State { get; init; }
        public string? Error { get; init; }

        [FromQuery(Name = "error_description")]
        public string? ErrorDescription { get; init; }
    }
}
