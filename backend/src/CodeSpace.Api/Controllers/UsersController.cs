using CodeSpace.Messages.Queries.Users;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace CodeSpace.Api.Controllers;

[ApiController]
[Route("api/users")]
public class UsersController : ControllerBase
{
    private readonly IMediator _mediator;

    public UsersController(IMediator mediator) { _mediator = mediator; }

    /// <summary>Current authenticated user + their teams. Reads from JWT; no X-Team-Id required.</summary>
    [HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new MeQuery(), cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }
}
