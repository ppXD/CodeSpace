using CodeSpace.Messages.Commands.Workflows;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CodeSpace.Api.Controllers;

/// <summary>
/// Unauthenticated callback surface: an external system resumes a run parked on
/// <c>flow.wait_callback</c> by POSTing to the tokened URL the run-detail UI shows. The token is
/// the bearer secret (high-entropy, single-use while the wait is pending) — there is no team /
/// user context, mirroring <see cref="WebhooksController"/>.
/// </summary>
[ApiController]
[Route("api/workflows/callbacks")]
[AllowAnonymous]
public class WorkflowCallbacksController : ControllerBase
{
    private readonly IMediator _mediator;

    public WorkflowCallbacksController(IMediator mediator) { _mediator = mediator; }

    [HttpPost("{token}")]
    public async Task<IActionResult> Resume([FromRoute] string token, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        var resumed = await _mediator.Send(new ResumeWorkflowCallbackCommand { Token = token, Body = body }, cancellationToken).ConfigureAwait(false);

        // 404 (not 200) when nothing matched — don't confirm whether a token exists / is still pending.
        return resumed ? Ok(new { resumed = true }) : NotFound();
    }
}
