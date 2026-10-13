using CodeSpace.Core.Services.OAuth;
using CodeSpace.Messages.Commands.Credentials;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CodeSpace.Api.Controllers;

/// <summary>
/// OAuth Authorization Code + PKCE flow for provider credentials.
///
/// <c>POST /init</c> is authenticated (the caller picks the target provider_instance).
/// <c>GET /callback</c> is anonymous — the provider's redirect can't carry our JWT. The proof
/// is two halves: the state (32-byte CSPRNG, one-time use, 10-min TTL, PK-indexed) and a nonce
/// cookie /init set in the browser that started the flow. State alone let whichever browser
/// presented it finish someone else's connect.
///
/// <para>A browser cannot always keep that cookie: a cross-site fetch response's SameSite=Lax cookie is never stored,
/// so with the SPA and the API on different sites — and a connect started on another host than <c>OAuth:CallbackUrl</c>
/// names — the callback arrives without it. It then consumes nothing and hands the code and state to the SPA's
/// completion page, which finishes the connect through <c>POST /complete</c> as the signed-in person who started it.</para>
/// </summary>
[ApiController]
[Route("api/credentials/oauth")]
public class CredentialOAuthController : ControllerBase
{
    /// <summary>The cookie that carries the starting browser's nonce from <c>/init</c> to <c>/callback</c>.</summary>
    public const string BrowserNonceCookie = "codespace_oauth_nonce";

    private readonly IMediator _mediator;

    public CredentialOAuthController(IMediator mediator) { _mediator = mediator; }

    [HttpPost("init")]
    public async Task<IActionResult> Init([FromBody] InitCredentialOAuthCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);

        BindToThisBrowser(result);

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

        var result = await _mediator.Send(new CompleteCredentialOAuthCommand { Code = query.Code, State = query.State, BrowserNonce = Request.Cookies[BrowserNonceCookie] }, cancellationToken).ConfigureAwait(false);

        // Handed off: nothing was consumed, and a cookie this browser does hold belongs to another connect, so it stays.
        if (result.CredentialId is not { } credentialId) return Redirect(OAuthReturnUrl.HandOff(result.ReturnUrl, query.Code, query.State));

        // Spent with the state it proved.
        Response.Cookies.Delete(BrowserNonceCookie, BrowserNonceCookieOptions(result.BrowserNonceCookiePath, lifetime: null));

        return Redirect(BuildSuccessReturn(result.ReturnUrl, credentialId));
    }

    /// <summary>
    /// The SPA's completion page finishing a connect the callback handed off. Authenticated — the fallback policy — and
    /// accepted only from the person who started the connect.
    /// </summary>
    [HttpPost("complete")]
    public async Task<IActionResult> Complete([FromBody] CompleteCredentialOAuthAsInitiatorCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);

        return Ok(new { credentialId = result.CredentialId });
    }

    /// <summary>
    /// HttpOnly so no script on any page reads it; SameSite=Lax because the provider's redirect back is a top-level GET
    /// from another site, which Lax admits and Strict would not; and scoped to the callback's own path — the one request
    /// that needs it. The flow service derives that path from <c>OAuth:CallbackUrl</c>, the address the browser is
    /// actually sent to, so it holds behind a path-rewriting proxy.
    ///
    /// <para>Secure exactly when this request arrived over HTTPS — the effective scheme, after whatever forwarded-headers
    /// handling the host applies (ASP.NET's own <c>ASPNETCORE_FORWARDEDHEADERS_ENABLED</c> behind a TLS-terminating
    /// proxy). A browser discards a Secure cookie set over plain HTTP, and Safari does so on http://localhost too, so an
    /// unconditional Secure left every plain-HTTP deployment unable to finish a connect: the callback arrived without
    /// the nonce and was refused.</para>
    /// </summary>
    private void BindToThisBrowser(InitCredentialOAuthResult result) =>
        Response.Cookies.Append(BrowserNonceCookie, result.BrowserNonce, BrowserNonceCookieOptions(result.BrowserNonceCookiePath, result.Lifetime));

    private CookieOptions BrowserNonceCookieOptions(string path, TimeSpan? lifetime) => new()
    {
        HttpOnly = true,
        Secure = Request.IsHttps,
        SameSite = SameSiteMode.Lax,
        Path = path,
        MaxAge = lifetime,
        IsEssential = true,
    };

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
