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
    public async Task<IActionResult> Callback([FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error, [FromQuery(Name = "error_description")] string? errorDescription, CancellationToken cancellationToken)
    {
        // Provider rejected (e.g. user clicked deny). Bounce back to a generic UI page
        // with the provider's error code so the SPA can show a sensible message.
        if (!string.IsNullOrEmpty(error)) return Redirect(BuildErrorReturn("/", error, errorDescription));

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state)) return BadRequest(new { code = "oauth_callback_invalid", message = "missing code or state" });

        var result = await _mediator.Send(new CompleteCredentialOAuthCommand { Code = code, State = state }, cancellationToken).ConfigureAwait(false);

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
}
