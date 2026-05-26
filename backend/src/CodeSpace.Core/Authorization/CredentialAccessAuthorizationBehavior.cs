using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Authorization;

public sealed class CredentialAccessAuthorizationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : IRequireCredentialAccess
{
    private readonly CodeSpaceDbContext _db;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;
    private readonly TeamMembershipResolver _resolver;

    public CredentialAccessAuthorizationBehavior(CodeSpaceDbContext db, ICurrentTeam currentTeam, ICurrentUser currentUser, TeamMembershipResolver resolver)
    {
        _db = db;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
        _resolver = resolver;
    }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (_currentUser.HasRole(Roles.Admin)) return await next().ConfigureAwait(false);

        var headerTeamId = _currentTeam.Id ?? throw new TenantAccessDeniedException(_currentUser.Id, Guid.Empty, $"{HeaderCurrentTeam.HeaderName} header missing");

        var entityTeamId = await _db.Credential.AsNoTracking()
            .Where(c => c.Id == request.CredentialId && c.DeletedDate == null)
            .Select(c => (Guid?)c.TeamId)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (entityTeamId == null) throw new TenantAccessDeniedException(_currentUser.Id, headerTeamId, $"credential {request.CredentialId} not found or not accessible");

        if (entityTeamId.Value != headerTeamId) throw new TenantAccessDeniedException(_currentUser.Id, headerTeamId, $"credential belongs to a different team than {HeaderCurrentTeam.HeaderName}");

        await _resolver.EnsureMembershipAsync(headerTeamId, cancellationToken).ConfigureAwait(false);

        return await next().ConfigureAwait(false);
    }
}
