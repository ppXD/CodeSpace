using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Sessions.Journal;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Queries.Sessions;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Sessions;

/// <summary>Thin (Rule 16): read the one model call's detail, team-scoped. A foreign / missing run or unknown sequence → null (→ 404).</summary>
public sealed class GetModelCallDetailQueryHandler : IRequestHandler<GetModelCallDetailQuery, ModelCallDetail?>
{
    private readonly IModelCallDetailReader _reader;
    private readonly ICurrentTeam _currentTeam;

    public GetModelCallDetailQueryHandler(IModelCallDetailReader reader, ICurrentTeam currentTeam)
    {
        _reader = reader;
        _currentTeam = currentTeam;
    }

    public async Task<ModelCallDetail?> Handle(GetModelCallDetailQuery request, CancellationToken cancellationToken) =>
        await _reader.ReadAsync(request.RunId, request.Sequence, _currentTeam.Id!.Value, cancellationToken).ConfigureAwait(false);
}
