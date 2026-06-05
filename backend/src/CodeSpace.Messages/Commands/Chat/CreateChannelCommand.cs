using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;
using MediatR;

namespace CodeSpace.Messages.Commands.Chat;

/// <summary>
/// Create a named channel under the caller's team. The creator becomes the channel Owner.
/// <see cref="Slug"/> is normalized server-side (lowercase, url-safe) and must be unique per
/// team — the service throws a typed error on collision so the operator picks another.
/// </summary>
public sealed record CreateChannelCommand : ICommand<Guid>, IRequireTeamMembership
{
    public required string Name { get; init; }
    public required string Slug { get; init; }

    /// <summary>Private channels are visible only to members; public channels appear in the
    /// directory and any team member may join.</summary>
    public bool Private { get; init; }
}
