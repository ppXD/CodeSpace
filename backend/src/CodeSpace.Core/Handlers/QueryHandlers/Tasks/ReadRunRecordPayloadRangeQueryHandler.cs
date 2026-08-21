using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Tasks.Trace;
using CodeSpace.Messages.Queries.Tasks;
using CodeSpace.Messages.Tasks.Trace;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Tasks;

public sealed class ReadRunRecordPayloadRangeQueryHandler : IRequestHandler<ReadRunRecordPayloadRangeQuery, RunRecordPayloadRangeRead?>
{
    private readonly IRunRecordPayloadReader _reader;
    private readonly ICurrentTeam _currentTeam;

    public ReadRunRecordPayloadRangeQueryHandler(IRunRecordPayloadReader reader, ICurrentTeam currentTeam)
    {
        _reader = reader;
        _currentTeam = currentTeam;
    }

    public async Task<RunRecordPayloadRangeRead?> Handle(ReadRunRecordPayloadRangeQuery request, CancellationToken cancellationToken) =>
        await _reader.ReadAsync(new RunRecordPayloadRangeRequest(request.RunId, _currentTeam.Id!.Value, request.RecordId,
            request.OffsetBytes, request.LimitBytes), cancellationToken).ConfigureAwait(false);
}
