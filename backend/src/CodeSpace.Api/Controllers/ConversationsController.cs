using CodeSpace.Messages.Commands.Chat;
using CodeSpace.Messages.Queries.Chat;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace CodeSpace.Api.Controllers;

/// <summary>
/// REST surface for team-scoped chat conversations — list / get / create channel / open DM /
/// create group / add member. Team scope comes from <c>X-Team-Id</c>; the MediatR pipeline
/// vets membership before the handler runs. Commands / queries bind directly per Rule 17.
/// </summary>
[ApiController]
[Route("api/conversations")]
public class ConversationsController : ControllerBase
{
    private readonly IMediator _mediator;

    public ConversationsController(IMediator mediator) { _mediator = mediator; }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListConversationsQuery(), cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpGet("{conversationId:guid}")]
    public async Task<IActionResult> Get([FromRoute] Guid conversationId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetConversationQuery { ConversationId = conversationId }, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }

    [HttpPost("channels")]
    public async Task<IActionResult> CreateChannel([FromBody] CreateChannelCommand command, CancellationToken cancellationToken)
    {
        var id = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return CreatedAtAction(nameof(Get), new { conversationId = id }, new { id });
    }

    [HttpPost("direct")]
    public async Task<IActionResult> OpenDirect([FromBody] OpenDirectConversationCommand command, CancellationToken cancellationToken)
    {
        var id = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return CreatedAtAction(nameof(Get), new { conversationId = id }, new { id });
    }

    [HttpPost("groups")]
    public async Task<IActionResult> CreateGroup([FromBody] CreateGroupConversationCommand command, CancellationToken cancellationToken)
    {
        var id = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return CreatedAtAction(nameof(Get), new { conversationId = id }, new { id });
    }

    [HttpPost("{conversationId:guid}/members")]
    public async Task<IActionResult> AddMember([FromRoute] Guid conversationId, [FromBody] AddConversationMemberCommand command, CancellationToken cancellationToken)
    {
        await _mediator.Send(command with { ConversationId = conversationId }, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }
}
