using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Queries.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Agents;

public sealed class ListAgentRunEventsQueryHandler : IRequestHandler<ListAgentRunEventsQuery, IReadOnlyList<AgentRunEventDto>>
{
    private readonly IAgentRunService _runs;
    private readonly ICurrentTeam _currentTeam;

    public ListAgentRunEventsQueryHandler(IAgentRunService runs, ICurrentTeam currentTeam)
    {
        _runs = runs;
        _currentTeam = currentTeam;
    }

    public async Task<IReadOnlyList<AgentRunEventDto>> Handle(ListAgentRunEventsQuery request, CancellationToken cancellationToken)
    {
        var events = await _runs.GetEventsAsync(request.AgentRunId, _currentTeam.Id!.Value, request.AfterSequence, cancellationToken).ConfigureAwait(false);

        return events.Select(e => new AgentRunEventDto
        {
            Sequence = e.Sequence,
            Kind = e.Kind,
            Text = e.Text,
            Data = e.DataJson,
            DataArtifactId = e.DataArtifactId,
            OccurredAt = e.OccurredAt,
        }).ToList();
    }
}
