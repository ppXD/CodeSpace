using CodeSpace.Core.Services.Teams;
using CodeSpace.Messages.Commands.Teams;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Teams;

public sealed class ChangeTeamMemberRoleCommandHandler : IRequestHandler<ChangeTeamMemberRoleCommand, Unit>
{
    private readonly ITeamMemberService _members;

    public ChangeTeamMemberRoleCommandHandler(ITeamMemberService members) { _members = members; }

    public async Task<Unit> Handle(ChangeTeamMemberRoleCommand request, CancellationToken cancellationToken)
    {
        await _members.ChangeRoleAsync(request.UserId, request.Role, cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}

public sealed class RemoveTeamMemberCommandHandler : IRequestHandler<RemoveTeamMemberCommand, Unit>
{
    private readonly ITeamMemberService _members;

    public RemoveTeamMemberCommandHandler(ITeamMemberService members) { _members = members; }

    public async Task<Unit> Handle(RemoveTeamMemberCommand request, CancellationToken cancellationToken)
    {
        await _members.RemoveAsync(request.UserId, cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}

public sealed class LeaveTeamCommandHandler : IRequestHandler<LeaveTeamCommand, Unit>
{
    private readonly ITeamMemberService _members;

    public LeaveTeamCommandHandler(ITeamMemberService members) { _members = members; }

    public async Task<Unit> Handle(LeaveTeamCommand request, CancellationToken cancellationToken)
    {
        await _members.LeaveAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}

public sealed class TransferTeamOwnershipCommandHandler : IRequestHandler<TransferTeamOwnershipCommand, Unit>
{
    private readonly ITeamMemberService _members;

    public TransferTeamOwnershipCommandHandler(ITeamMemberService members) { _members = members; }

    public async Task<Unit> Handle(TransferTeamOwnershipCommand request, CancellationToken cancellationToken)
    {
        await _members.TransferOwnershipAsync(request.ToUserId, cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
