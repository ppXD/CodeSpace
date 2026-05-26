using CodeSpace.Messages.Commands.ProviderInstances;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.ProviderInstances;
using CodeSpace.Messages.Queries.Providers;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace CodeSpace.Api.Controllers;

[ApiController]
[Route("api/provider-instances")]
public class ProviderInstancesController : ControllerBase
{
    private readonly IMediator _mediator;

    public ProviderInstancesController(IMediator mediator) { _mediator = mediator; }

    // All endpoints require X-Team-Id header (enforced by TeamMembershipAuthorizationBehavior).

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListProviderInstancesQuery(), cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> Add([FromBody] AddProviderInstanceCommand command, CancellationToken cancellationToken)
    {
        var id = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return Ok(new { id });
    }

    /// <summary>
    /// PATCH-style update — only fields the caller actually changed flow through. Sending an
    /// empty OauthClientSecret keeps the stored value (so the form's empty password input
    /// doesn't accidentally clear the secret on every save).
    /// </summary>
    [HttpPatch("{providerInstanceId:guid}")]
    public async Task<IActionResult> Update([FromRoute] Guid providerInstanceId, [FromBody] UpdateProviderInstanceCommand body, CancellationToken cancellationToken)
    {
        // Route id is authoritative — body.ProviderInstanceId is bound from JSON but we
        // overwrite with the route value so a body/route mismatch can't be abused.
        await _mediator.Send(body with { ProviderInstanceId = providerInstanceId }, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    [HttpDelete("{providerInstanceId:guid}")]
    public async Task<IActionResult> Delete([FromRoute] Guid providerInstanceId, [FromQuery] DeleteProviderInstanceCommand command, CancellationToken cancellationToken)
    {
        // Route + query merge — query string carries `force`; route carries the id.
        var result = await _mediator.Send(command with { ProviderInstanceId = providerInstanceId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Pre-delete preview: how many repos / credentials will be touched. UI uses this to
    /// put concrete numbers in the "Remove provider?" confirm dialog.
    /// </summary>
    [HttpGet("{providerInstanceId:guid}/usage")]
    public async Task<IActionResult> Usage([FromRoute] Guid providerInstanceId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetProviderInstanceUsageQuery { ProviderInstanceId = providerInstanceId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Returns the provider module's hard-coded defaults — recommended base URL, default
    /// OAuth scope list, callback URL the operator must paste into the provider app config.
    /// Lets the frontend skip duplicating these strings; scope renames in a module land in
    /// the UI on next render with no manual sync. Requires only auth (no team membership).
    /// </summary>
    [HttpGet("defaults/{provider}")]
    public async Task<IActionResult> Defaults([FromRoute] ProviderKind provider, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetProviderDefaultsQuery { Provider = provider }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }
}
