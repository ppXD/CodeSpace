using CodeSpace.Api.Extensions;
using CodeSpace.Api.Http;
using CodeSpace.Messages.Commands.Workflows;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CodeSpace.Api.Controllers;

/// <summary>
/// Unauthenticated callback surface: an external system resumes a run parked on
/// <c>flow.wait_callback</c> by POSTing to the tokened URL the run-detail UI shows. The token is
/// the bearer secret (high-entropy, single-use while the wait is pending) — there is no team /
/// user context, mirroring <see cref="WebhooksController"/>, and bounded the same way: rate-limited
/// per address and then per (token, address), and read only up to <see cref="MaxCallbackBodyBytes"/>.
/// </summary>
[ApiController]
[Route("api/workflows/callbacks")]
[AllowAnonymous]
[EnableRateLimiting(WebhookIngressRateLimitExtension.PolicyName)]
public class WorkflowCallbacksController : ControllerBase
{
    /// <summary>The body becomes the waiting node's output, stored in a jsonb row and read by every later step. A resume signal has no business being larger.</summary>
    public const long MaxCallbackBodyBytes = 1024 * 1024;

    private readonly IMediator _mediator;

    public WorkflowCallbacksController(IMediator mediator) { _mediator = mediator; }

    /// <summary>
    /// The route admits only the shape the engine mints — <c>Guid.NewGuid().ToString("N")</c>, 32 characters. A token no
    /// wait can hold is answered 404 by routing, before the limiter keys a partition on it or anything looks it up.
    /// </summary>
    [HttpPost("{token:length(32)}")]
    [RequestSizeLimit(MaxCallbackBodyBytes)]
    public async Task<IActionResult> Resume([FromRoute] string token, CancellationToken cancellationToken)
    {
        var body = await Request.ReadBoundedBodyAsync(MaxCallbackBodyBytes, cancellationToken).ConfigureAwait(false);

        if (body == null) return StatusCode(StatusCodes.Status413PayloadTooLarge);

        var resumed = await _mediator.Send(new ResumeWorkflowCallbackCommand { Token = token, Body = body }, cancellationToken).ConfigureAwait(false);

        // 404 (not 200) when nothing matched — don't confirm whether a token exists / is still pending.
        return resumed ? Ok(new { resumed = true }) : NotFound();
    }
}
