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
}
