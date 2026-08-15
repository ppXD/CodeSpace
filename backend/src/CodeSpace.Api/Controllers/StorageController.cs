using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Queries.Storage;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace CodeSpace.Api.Controllers;

/// <summary>Authenticated, team-scoped discovery surfaces for storage configuration.</summary>
[ApiController]
[Route("api/storage")]
public class StorageController : ControllerBase
{
    private readonly IMediator _mediator;

    public StorageController(IMediator mediator) { _mediator = mediator; }

    /// <summary>
    /// Provider types available in this build, including public configuration and write-only-input schemas. Returns
    /// descriptor metadata only — never profile/secret values or the module's runtime factory type.
    /// </summary>
    [HttpGet("provider-modules")]
    public async Task<IActionResult> ListProviderModules(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListStorageProviderModulesQuery(), cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpGet("profiles")]
    public async Task<IActionResult> ListProfiles(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListStorageProfilesQuery(), cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpGet("profiles/{profileId:guid}")]
    public async Task<IActionResult> GetProfile([FromRoute] Guid profileId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetStorageProfileQuery { ProfileId = profileId }, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }

    [HttpPost("profiles")]
    public async Task<IActionResult> CreateProfile([FromBody] CreateStorageProfileCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpPost("profiles/{profileId:guid}/revisions")]
    public async Task<IActionResult> AppendProfileRevision([FromRoute] Guid profileId, [FromBody] AppendStorageProfileRevisionCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command with { ProfileId = profileId }, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }

    [HttpPut("profiles/{profileId:guid}/state")]
    public async Task<IActionResult> SetProfileState([FromRoute] Guid profileId, [FromBody] SetStorageProfileStateCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command with { ProfileId = profileId }, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }
}
