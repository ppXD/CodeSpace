using CodeSpace.Messages.Authorization;
using MediatR;

namespace CodeSpace.Messages.Commands.Chat;

/// <summary>
/// Soft-delete a message — only the author may. The row is kept (rendered as a tombstone so
/// threads stay continuous) but its reference rows are dropped so the deleted message stops
/// appearing as a backlink on whatever it mentioned.
/// </summary>
public sealed record DeleteMessageCommand : IRequest, IRequireTeamMembership
{
    public Guid MessageId { get; init; }
}
