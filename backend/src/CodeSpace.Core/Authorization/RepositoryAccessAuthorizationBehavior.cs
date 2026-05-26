using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Authorization;

public sealed class RepositoryAccessAuthorizationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : IRequireRepositoryAccess
{
    private readonly CodeSpaceDbContext _db;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;
    private readonly TeamMembershipResolver _resolver;

    public RepositoryAccessAuthorizationBehavior(CodeSpaceDbContext db, ICurrentTeam currentTeam, ICurrentUser currentUser, TeamMembershipResolver resolver)
    {
        _db = db;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
        _resolver = resolver;
    }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        // Admin role bypasses every check INCLUDING the FK dereference. Background jobs
        // legitimately act on any repo and "row missing" should surface from the handler.
        if (_currentUser.HasRole(Roles.Admin)) return await next().ConfigureAwait(false);

        var headerTeamId = _currentTeam.Id ?? throw new TenantAccessDeniedException(_currentUser.Id, Guid.Empty, $"{HeaderCurrentTeam.HeaderName} header missing");

        var entityTeamId = await _db.Repository.AsNoTracking()
            .Where(r => r.Id == request.RepositoryId && r.DeletedDate == null)
            .Select(r => (Guid?)r.TeamId)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        // Conflate not-found with no-access so attackers can't enumerate repositories by id.
        if (entityTeamId == null) throw new TenantAccessDeniedException(_currentUser.Id, headerTeamId, $"repository {request.RepositoryId} not found or not accessible");

        // Cross-check: header team must match the repo's team. Catches URL tampering across
        // teams the user happens to belong to.
        if (entityTeamId.Value != headerTeamId) throw new TenantAccessDeniedException(_currentUser.Id, headerTeamId, $"repository belongs to a different team than {HeaderCurrentTeam.HeaderName}");

        await _resolver.EnsureMembershipAsync(headerTeamId, cancellationToken).ConfigureAwait(false);

        return await next().ConfigureAwait(false);
    }
}
