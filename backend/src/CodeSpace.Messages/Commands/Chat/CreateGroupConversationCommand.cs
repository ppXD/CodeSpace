using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;
using MediatR;

namespace CodeSpace.Messages.Commands.Chat;

/// <summary>
/// Create an ad-hoc group conversation. The caller is auto-included as Owner; the resulting
/// member set is the distinct union of <see cref="MemberUserIds"/> + the caller. Requires at
/// least two distinct members total.
/// </summary>
public sealed record CreateGroupConversationCommand : ICommand<Guid>, IRequireTeamMembership
{
    public string? Name { get; init; }
    public required IReadOnlyList<Guid> MemberUserIds { get; init; }
}
