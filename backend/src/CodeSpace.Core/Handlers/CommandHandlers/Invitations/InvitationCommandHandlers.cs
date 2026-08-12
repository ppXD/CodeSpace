using CodeSpace.Core.Services.Invitations;
using CodeSpace.Core.Settings.Invitations;
using CodeSpace.Messages.Commands.Auth;
using CodeSpace.Messages.Commands.Invitations;
using CodeSpace.Messages.Dtos.Invitations;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Invitations;

public sealed class CreateTeamInvitationCommandHandler : IRequestHandler<CreateTeamInvitationCommand, CreateInvitationResult>
{
    private readonly ITeamInvitationService _invitations;
    private readonly InviteUrlTemplateSetting _template;

    public CreateTeamInvitationCommandHandler(ITeamInvitationService invitations, InviteUrlTemplateSetting template)
    {
        _invitations = invitations;
        _template = template;
    }

    public async Task<CreateInvitationResult> Handle(CreateTeamInvitationCommand request, CancellationToken cancellationToken) =>
        await _invitations.InviteAsync(request.Email, request.Role, _template.Value, cancellationToken).ConfigureAwait(false);
}

public sealed class RevokeTeamInvitationCommandHandler : IRequestHandler<RevokeTeamInvitationCommand, Unit>
{
    private readonly ITeamInvitationService _invitations;

    public RevokeTeamInvitationCommandHandler(ITeamInvitationService invitations) { _invitations = invitations; }

    public async Task<Unit> Handle(RevokeTeamInvitationCommand request, CancellationToken cancellationToken)
    {
        await _invitations.RevokeAsync(request.InvitationId, cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}

public sealed class RegenerateTeamInvitationCommandHandler : IRequestHandler<RegenerateTeamInvitationCommand, CreateInvitationResult>
{
    private readonly ITeamInvitationService _invitations;
    private readonly InviteUrlTemplateSetting _template;

    public RegenerateTeamInvitationCommandHandler(ITeamInvitationService invitations, InviteUrlTemplateSetting template)
    {
        _invitations = invitations;
        _template = template;
    }

    public async Task<CreateInvitationResult> Handle(RegenerateTeamInvitationCommand request, CancellationToken cancellationToken) =>
        await _invitations.RegenerateAsync(request.InvitationId, _template.Value, cancellationToken).ConfigureAwait(false);
}

public sealed class AcceptInvitationCommandHandler : IRequestHandler<AcceptInvitationCommand, SignInResponse>
{
    private readonly ITeamInvitationService _invitations;

    public AcceptInvitationCommandHandler(ITeamInvitationService invitations) { _invitations = invitations; }

    public async Task<SignInResponse> Handle(AcceptInvitationCommand request, CancellationToken cancellationToken) =>
        await _invitations.AcceptAsync(request.Token, request.Name, request.Password, cancellationToken).ConfigureAwait(false);
}
