using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Chat;
using MediatR;

namespace CodeSpace.Messages.Commands.Chat;

/// <summary>
/// Replace a message's body. Only the original author may edit (the service enforces it). The
/// reference rows are re-derived from the new body in the same transaction, so editing a mention
/// in or out keeps the reverse index exact. Sets the "(edited)" marker. Returns the updated view.
/// </summary>
public sealed record EditMessageCommand : IRequest<MessageView>, IRequireTeamMembership
{
    public Guid MessageId { get; init; }
    public required string Body { get; init; }
}
