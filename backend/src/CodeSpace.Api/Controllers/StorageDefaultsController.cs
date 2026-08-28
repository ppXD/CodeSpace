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
/// <para><b>Authoring a template does not move any team.</b> A team is materialized from one only when its own admin
/// adopts it through <c>POST api/storage/adoptions</c> — or, for a class whose template declares an Automatic policy,
/// on that team's first write. Editing or disabling a template changes what a LATER materialization will produce and
/// never touches a team already on it: reads resolve through the profile revision recorded at write time.</para>
/// </summary>
[ApiController]
[Route("api/admin/storage-defaults")]
public class StorageDefaultsController : ControllerBase
{
    internal const long MaxMutationBodyBytes = 128 * 1024;
    private readonly IMediator _mediator;

    public StorageDefaultsController(IMediator mediator) { _mediator = mediator; }

    /// <summary>The installed provider catalog, under this controller's own capability — an operator who authors templates need not belong to any team.</summary>
    [HttpGet("provider-modules")]
    public async Task<IActionResult> ListProviderModules(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListStorageDefaultProviderModulesQuery(), cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>The routed data classes a template may be authored for, under this controller's own capability.</summary>
    [HttpGet("data-classes")]
    public async Task<IActionResult> ListDataClasses(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListStorageDefaultDataClassesQuery(), cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

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
