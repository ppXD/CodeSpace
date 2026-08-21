using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Tasks.Trace;
using CodeSpace.Messages.Queries.Tasks;
using CodeSpace.Messages.Tasks.Trace;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Tasks;

public sealed class GetRunRecordPageQueryHandler : IRequestHandler<GetRunRecordPageQuery, RunRecordPageResponse?>
{
    private readonly IRunRecordPageReader _reader;
    private readonly ICurrentTeam _currentTeam;

    public GetRunRecordPageQueryHandler(IRunRecordPageReader reader, ICurrentTeam currentTeam)
    {
        _reader = reader;
        _currentTeam = currentTeam;
    }

    public async Task<RunRecordPageResponse?> Handle(GetRunRecordPageQuery request, CancellationToken cancellationToken) =>
        await _reader.ReadAsync(new RunRecordPageRequest(request.RunId, _currentTeam.Id!.Value, request.BeforeSequence, request.AfterSequence, request.Limit), cancellationToken).ConfigureAwait(false);
}
