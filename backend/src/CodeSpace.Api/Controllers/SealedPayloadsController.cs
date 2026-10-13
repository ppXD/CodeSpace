using CodeSpace.Messages.Commands.Workflows;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace CodeSpace.Api.Controllers;

/// <summary>
/// Deployment maintenance for sealed payloads (0247). Instance-level and global-admin only, enforced by the command's
/// mediator marker — it spans every team, so nothing here reads <c>X-Team-Id</c>.
/// </summary>
[ApiController]
[Route("api/admin/sealed-payloads")]
public class SealedPayloadsController : ControllerBase
{
    private readonly IMediator _mediator;

    public SealedPayloadsController(IMediator mediator) { _mediator = mediator; }

    /// <summary>The pre-rollback step for workers: restore every sealed row a previous-version worker could still execute — non-terminal run payloads, queued agent tasks — to the exact, unsealed shape it executes. Send it once no current worker is live (plus 30 seconds) and before starting the previous ones; <see cref="UnsealNonTerminalPayloadsCommand"/> has the order.</summary>
    [HttpPost("unseal")]
    public async Task<IActionResult> Unseal(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new UnsealNonTerminalPayloadsCommand(), cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }
}
