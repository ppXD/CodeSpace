using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Tasks.Trace;
using CodeSpace.Messages.Queries.Tasks;
using CodeSpace.Messages.Tasks.Trace;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Tasks;

/// <summary>
/// Thin dispatcher (Rule 16) — scopes to the CALLER'S team (<see cref="ICurrentTeam"/>, never the wire) and reads the
/// run's raw ledger via <see cref="IRunRecordReader"/>. A foreign / absent run → the reader returns null → this
/// returns null → the controller 404-conflates (no existence leak). All read + tenancy logic lives in the reader.
/// </summary>
public sealed class GetRunRecordsQueryHandler : IRequestHandler<GetRunRecordsQuery, RunRecordsResponse?>
{
    private readonly IRunRecordReader _reader;
    private readonly ICurrentTeam _currentTeam;

    public GetRunRecordsQueryHandler(IRunRecordReader reader, ICurrentTeam currentTeam)
    {
        _reader = reader;
        _currentTeam = currentTeam;
    }

    public async Task<RunRecordsResponse?> Handle(GetRunRecordsQuery request, CancellationToken cancellationToken) =>
        await _reader.ReadAsync(request.RunId, _currentTeam.Id!.Value, cancellationToken).ConfigureAwait(false);
}
