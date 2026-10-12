using CodeSpace.Core.Services.Completion;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Queries.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Agents;

/// <summary>Thin dispatcher (Rule 16) — the production caller of <see cref="IQualificationClaimBoardReader.ReadAsync"/>. The standings are platform-level and the query's membership marker is the read gate; the reader keeps each paying team's identifiers to that team.</summary>
public sealed class GetQualificationClaimsQueryHandler : IRequestHandler<GetQualificationClaimsQuery, QualificationClaimBoard>
{
    private readonly IQualificationClaimBoardReader _board;

    public GetQualificationClaimsQueryHandler(IQualificationClaimBoardReader board)
    {
        _board = board;
    }

    public async Task<QualificationClaimBoard> Handle(GetQualificationClaimsQuery request, CancellationToken cancellationToken)
    {
        return await _board.ReadAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
    }
}
