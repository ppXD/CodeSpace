using CodeSpace.Messages.Authorization;
using MediatR;

namespace CodeSpace.Messages.Commands.Chat;

/// <summary>
/// Open (or resolve the existing) 1-on-1 DM with another team member. Idempotent — returns
/// the same conversation id every time, so the frontend can call it on every "message this
/// person" click without creating duplicates.
/// </summary>
public sealed record OpenDirectConversationCommand : IRequest<Guid>, IRequireTeamMembership
{
    public required Guid OtherUserId { get; init; }
}
