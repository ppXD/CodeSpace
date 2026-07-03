using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Sessions.Journal;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Queries.Sessions;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Sessions;

/// <summary>Thin (Rule 16): project the session's journal (optionally focusing a run), then trim it to the <c>?since=</c> delta. A missing session → null (→ 404).</summary>
public sealed class GetSessionJournalQueryHandler : IRequestHandler<GetSessionJournalQuery, JournalView?>
{
    private readonly IJournalProjector _projector;
    private readonly ICurrentTeam _currentTeam;

    public GetSessionJournalQueryHandler(IJournalProjector projector, ICurrentTeam currentTeam)
    {
        _projector = projector;
        _currentTeam = currentTeam;
    }

    public async Task<JournalView?> Handle(GetSessionJournalQuery request, CancellationToken cancellationToken)
    {
        var view = await _projector.ProjectAsync(request.SessionId, request.FocusRunId, _currentTeam.Id!.Value, cancellationToken).ConfigureAwait(false);

        return view is null ? null : JournalDelta.After(view, request.Since);
    }
}
