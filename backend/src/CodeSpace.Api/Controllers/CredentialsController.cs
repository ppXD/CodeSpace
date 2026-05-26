using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Queries.Credentials;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace CodeSpace.Api.Controllers;

[ApiController]
[Route("api/credentials")]
public class CredentialsController : ControllerBase
{
    private readonly IMediator _mediator;

    public CredentialsController(IMediator mediator) { _mediator = mediator; }

    // All endpoints require X-Team-Id header (enforced by pipeline behaviors).

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] ListCredentialsQuery query, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(query, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpGet("{credentialId:guid}/accessible-repositories")]
    public async Task<IActionResult> ListAccessibleRepositories([FromRoute] Guid credentialId, [FromQuery] ListAccessibleRepositoriesQuery query, CancellationToken cancellationToken)
    {
        // Route's credentialId is authoritative; merge into the query record so the rest
        // of the contract (search / page / perPage with their record defaults) flows
        // directly from the query string without per-field unpacking.
        var result = await _mediator.Send(query with { CredentialId = credentialId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Per-capability availability snapshot for one credential — what the granted OAuth
    /// scopes can and can't do. Drives UI badges ("✓ Read · ⚠ Webhooks") so the operator
    /// sees at a glance which features will work before they hit a 422 mid-flow.
    /// </summary>
    [HttpGet("{credentialId:guid}/capabilities")]
    public async Task<IActionResult> Capabilities([FromRoute] Guid credentialId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetCredentialCapabilitiesQuery { CredentialId = credentialId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// PAT credentials need a wire-shape translation step — the body carries a single
    /// flat <c>token</c> string, the command needs a structured <see cref="PatPayload"/>.
    /// This translation is the intentional exception Rule 17 calls out: a sibling DTO
    /// (AddPatCredentialRequest) shapes the wire, and the controller maps it to the
    /// internal command. Future "AddOAuthCredential" / "AddSshKey" endpoints would each
    /// get their own request DTO.
    /// </summary>
    [HttpPost("pat")]
    public async Task<IActionResult> AddPat([FromBody] AddPatCredentialRequest request, CancellationToken cancellationToken)
    {
        var id = await _mediator.Send(new AddCredentialCommand
        {
            ProviderInstanceId = request.ProviderInstanceId,
            OwnerUserId = request.OwnerUserId,
            DisplayName = request.DisplayName,
            Payload = new PatPayload { Token = request.Token }
        }, cancellationToken).ConfigureAwait(false);

        return Ok(new { id });
    }

    [HttpPost("{credentialId:guid}/revoke")]
    public async Task<IActionResult> Revoke([FromRoute] Guid credentialId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new RevokeCredentialCommand { CredentialId = credentialId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Pre-revoke preview: how many repositories will be marked Error if this credential
    /// gets disconnected. UI uses this to put the right number in the confirm dialog so
    /// the user isn't surprised by a silent cascade.
    /// </summary>
    [HttpGet("{credentialId:guid}/usage")]
    public async Task<IActionResult> Usage([FromRoute] Guid credentialId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetCredentialUsageQuery { CredentialId = credentialId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }
}
