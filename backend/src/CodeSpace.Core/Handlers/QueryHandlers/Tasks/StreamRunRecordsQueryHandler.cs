using CodeSpace.Core.Services.Tasks.Trace;
using CodeSpace.Messages.Queries.Tasks;
using CodeSpace.Messages.Tasks.Trace;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Tasks;

/// <summary>
/// Thin dispatcher (Rule 16) — hands back the streamer's lazy tail. Nothing runs here: the authorization behaviors
/// have already vetted the caller by the time this returns, and the tail starts only when the caller enumerates it.
/// </summary>
public sealed class StreamRunRecordsQueryHandler : IRequestHandler<StreamRunRecordsQuery, IAsyncEnumerable<RunRecordView>>
{
    private readonly IRunRecordStreamer _streamer;

    public StreamRunRecordsQueryHandler(IRunRecordStreamer streamer) { _streamer = streamer; }

    public Task<IAsyncEnumerable<RunRecordView>> Handle(StreamRunRecordsQuery request, CancellationToken cancellationToken) =>
        Task.FromResult(_streamer.TailAsync(request.RunId, request.AfterSequence, cancellationToken));
}
