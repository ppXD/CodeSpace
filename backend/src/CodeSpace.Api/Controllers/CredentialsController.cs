using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Credentials;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

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

    /// <summary>
    /// Add a GitLab Group Access Token as a TEAM-SERVICE credential — owned by the team, not a
    /// person, so a repo bound through it survives anyone leaving. Same flat-token → structured-
    /// payload mapping as <see cref="AddPat"/> (Rule 17 exception). Ownership is forced TeamService,
    /// so the credential is owner-less regardless of who creates it.
    /// </summary>
    [HttpPost("group-access-token")]
    public async Task<IActionResult> AddGroupAccessToken([FromBody] AddGroupAccessTokenRequest request, CancellationToken cancellationToken)
    {
        var id = await _mediator.Send(new AddCredentialCommand
        {
            ProviderInstanceId = request.ProviderInstanceId,
            DisplayName = request.DisplayName,
            Payload = new GroupAccessTokenPayload { Token = request.Token },
            Ownership = CredentialOwnership.TeamService
        }, cancellationToken).ConfigureAwait(false);

        return Ok(new { id });
    }

    /// <summary>
    /// Disconnect. The body is optional — a client that sends none behaves as it always has — and may name where the
    /// credential's repositories and group hooks go (<c>moveBindingsToCredentialId</c>). Route's credentialId is
    /// authoritative: merged into the command with <c>with</c> so a body that disagrees cannot redirect the revoke.
    /// </summary>
    [HttpPost("{credentialId:guid}/revoke")]
    public async Task<IActionResult> Revoke([FromRoute] Guid credentialId, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] RevokeCredentialCommand? command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send((command ?? new RevokeCredentialCommand()) with { CredentialId = credentialId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Move every repository and group hook still bound through a dead credential onto another credential in one action.
    /// Route's fromCredentialId is authoritative; the body carries <c>toCredentialId</c>.
    /// </summary>
    [HttpPost("{fromCredentialId:guid}/relink-bindings")]
    public async Task<IActionResult> RelinkBindings([FromRoute] Guid fromCredentialId, [FromBody] RelinkCredentialBindingsCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command with { FromCredentialId = fromCredentialId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>What leans on the credential — repositories with their projects, group hooks, act-as identities, and the workflow triggers naming those repositories. Live or dead.</summary>
    [HttpGet("{credentialId:guid}/dependents")]
    public async Task<IActionResult> Dependents([FromRoute] Guid credentialId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetCredentialDependentsQuery { CredentialId = credentialId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>The credentials the caller could move this one's bindings onto, best first: their own live connections, then the team's service tokens.</summary>
    [HttpGet("{credentialId:guid}/relink-suggestions")]
    public async Task<IActionResult> RelinkSuggestions([FromRoute] Guid credentialId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetRelinkSuggestionsQuery { CredentialId = credentialId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Pre-revoke preview: what a Disconnect of this credential, asked by the caller right now, will do — how many
    /// repositories will be marked Error, or how many will instead move to the person's one other live connection.
    /// UI uses this to put the right number in the confirm dialog so the user isn't surprised by a silent cascade.
    /// </summary>
    [HttpGet("{credentialId:guid}/usage")]
    public async Task<IActionResult> Usage([FromRoute] Guid credentialId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetCredentialUsageQuery { CredentialId = credentialId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }
}
