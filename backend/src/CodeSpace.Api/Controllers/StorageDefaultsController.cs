using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Queries.Storage;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace CodeSpace.Api.Controllers;

/// <summary>
/// Deployment-wide storage defaults — the instance-admin template tier, gated by the
/// <c>storage.defaults.manage</c> instance capability rather than by team membership.
///
/// <para><b>Deliberately NOT under <c>api/storage</c>, and it must stay that way.</b>
/// <c>frontend/src/api/client.ts</c> injects <c>X-Team-Id</c> from local storage into every request and no non-team
/// route clears it, so an admin page calling a team-scoped controller would silently write into whatever team the
/// operator happened to visit last. A separate route keeps the ambient header inert here: nothing on this controller
/// reads a team.</para>
///
/// <para><b>Nothing consumes these templates yet.</b> No team resolves storage through one, no route is created from
/// one, and no byte moves because one exists — the materializer lane is the intended reader.</para>
/// </summary>
[ApiController]
[Route("api/admin/storage-defaults")]
public class StorageDefaultsController : ControllerBase
{
    internal const long MaxMutationBodyBytes = 128 * 1024;
    private readonly IMediator _mediator;

    public StorageDefaultsController(IMediator mediator) { _mediator = mediator; }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListStorageDefaultsQuery(), cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpGet("{defaultId:guid}")]
    public async Task<IActionResult> Get([FromRoute] Guid defaultId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetStorageDefaultQuery { DefaultId = defaultId }, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }

    [HttpPost]
    [RequestSizeLimit(MaxMutationBodyBytes)]
    public async Task<IActionResult> Create([FromBody] CreateStorageDefaultCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpPut("{defaultId:guid}")]
    [RequestSizeLimit(MaxMutationBodyBytes)]
    public async Task<IActionResult> Update([FromRoute] Guid defaultId, [FromBody] UpdateStorageDefaultCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command with { DefaultId = defaultId }, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }

    [HttpPut("{defaultId:guid}/enabled")]
    [RequestSizeLimit(MaxMutationBodyBytes)]
    public async Task<IActionResult> SetEnabled([FromRoute] Guid defaultId, [FromBody] SetStorageDefaultEnabledCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command with { DefaultId = defaultId }, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }
}
