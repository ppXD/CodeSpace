using CodeSpace.Core.Services.Invitations;
using CodeSpace.Messages.Dtos.Invitations;
using CodeSpace.Messages.Queries.Invitations;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Invitations;

public sealed class ListTeamInvitationsQueryHandler : IRequestHandler<ListTeamInvitationsQuery, IReadOnlyList<TeamInvitationSummary>>
{
    private readonly ITeamInvitationService _invitations;

    public ListTeamInvitationsQueryHandler(ITeamInvitationService invitations) { _invitations = invitations; }

    public async Task<IReadOnlyList<TeamInvitationSummary>> Handle(ListTeamInvitationsQuery request, CancellationToken cancellationToken) =>
        await _invitations.ListAsync(cancellationToken).ConfigureAwait(false);
}

public sealed class PreviewInvitationQueryHandler : IRequestHandler<PreviewInvitationQuery, InvitationPreview>
{
    private readonly ITeamInvitationService _invitations;

    public PreviewInvitationQueryHandler(ITeamInvitationService invitations) { _invitations = invitations; }

    public async Task<InvitationPreview> Handle(PreviewInvitationQuery request, CancellationToken cancellationToken) =>
        await _invitations.PreviewAsync(request.Token ?? string.Empty, cancellationToken).ConfigureAwait(false);
}
