using CodeSpace.Core.Services.Completion;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Queries.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Agents;

/// <summary>Thin dispatcher (Rule 16) — the production caller of <see cref="IQualificationClaimResolver.ResolveBoardAsync"/>. The board is platform-level; the query's membership marker is the read gate.</summary>
public sealed class GetQualificationClaimsQueryHandler : IRequestHandler<GetQualificationClaimsQuery, QualificationClaimBoard>
{
    private readonly IQualificationClaimResolver _claims;

    public GetQualificationClaimsQueryHandler(IQualificationClaimResolver claims)
    {
        _claims = claims;
    }

    public async Task<QualificationClaimBoard> Handle(GetQualificationClaimsQuery request, CancellationToken cancellationToken)
    {
        return await _claims.ResolveBoardAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
    }
}
