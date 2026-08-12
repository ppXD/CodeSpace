using CodeSpace.Messages.Commands.Accounts;
using CodeSpace.Messages.Queries.Accounts;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace CodeSpace.Api.Controllers;

/// <summary>
/// Instance-level account administration. Every action here is global-admin only, enforced by the
/// mediator's marker rather than by this class — it spans teams by definition, so team scope has
/// nothing to say about it.
/// </summary>
[ApiController]
[Route("api/admin/accounts")]
public class AccountsController : ControllerBase
{
    private readonly IMediator _mediator;

    public AccountsController(IMediator mediator) { _mediator = mediator; }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok(await _mediator.Send(new ListAccountsQuery(), cancellationToken).ConfigureAwait(false));

    [HttpPost("{userId:guid}/deactivate")]
    public async Task<IActionResult> Deactivate([FromRoute] Guid userId, CancellationToken cancellationToken)
    {
        await _mediator.Send(new DeactivateAccountCommand { UserId = userId }, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    [HttpPost("{userId:guid}/reactivate")]
    public async Task<IActionResult> Reactivate([FromRoute] Guid userId, CancellationToken cancellationToken)
    {
        await _mediator.Send(new ReactivateAccountCommand { UserId = userId }, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>Answers with the link ONCE. It is not stored and cannot be read again.</summary>
    [HttpPost("{userId:guid}/reset-link")]
    public async Task<IActionResult> IssueResetLink([FromRoute] Guid userId, CancellationToken cancellationToken) =>
        Ok(await _mediator.Send(new IssuePasswordResetCommand { UserId = userId }, cancellationToken).ConfigureAwait(false));
}
