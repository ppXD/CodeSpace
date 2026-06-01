using CodeSpace.Messages.Commands.Chat;
using CodeSpace.Messages.Queries.Chat;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace CodeSpace.Api.Controllers;

/// <summary>
/// REST surface for messages inside a conversation — send / list (keyset-paginated) / edit /
/// delete / mark-read. Conversation scope comes from the route; the MediatR pipeline vets team
/// membership and the service vets conversation membership before any handler runs. Commands /
/// queries bind directly (Rule 17) — the route's <c>conversationId</c> / <c>messageId</c> is
/// merged onto the record so the URL stays authoritative.
/// </summary>
[ApiController]
[Route("api/conversations/{conversationId:guid}/messages")]
public class MessagesController : ControllerBase
{
    private readonly IMediator _mediator;

    public MessagesController(IMediator mediator) { _mediator = mediator; }

    [HttpGet]
    public async Task<IActionResult> List([FromRoute] Guid conversationId, [FromQuery] ListMessagesQuery query, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(query with { ConversationId = conversationId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> Post([FromRoute] Guid conversationId, [FromBody] PostMessageCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command with { ConversationId = conversationId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpPut("{messageId:guid}")]
    public async Task<IActionResult> Edit([FromRoute] Guid messageId, [FromBody] EditMessageCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command with { MessageId = messageId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpDelete("{messageId:guid}")]
    public async Task<IActionResult> Delete([FromRoute] Guid messageId, CancellationToken cancellationToken)
    {
        await _mediator.Send(new DeleteMessageCommand { MessageId = messageId }, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    [HttpPost("read")]
    public async Task<IActionResult> MarkRead([FromRoute] Guid conversationId, [FromBody] MarkConversationReadCommand command, CancellationToken cancellationToken)
    {
        await _mediator.Send(command with { ConversationId = conversationId }, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Respond to an interactive message — click a card button. The body carries the chosen
    /// <c>responseKey</c> (+ optional comment); the route's <c>messageId</c> identifies the
    /// interaction. The wait token stays server-side — the service re-derives it from the message.
    /// </summary>
    [HttpPost("{messageId:guid}/respond")]
    public async Task<IActionResult> Respond([FromRoute] Guid messageId, [FromBody] RespondToMessageCommand command, CancellationToken cancellationToken)
    {
        await _mediator.Send(command with { MessageId = messageId }, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }
}
