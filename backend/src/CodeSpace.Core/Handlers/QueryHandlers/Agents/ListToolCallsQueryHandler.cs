using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Queries.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Agents;

public sealed class ListToolCallsQueryHandler : IRequestHandler<ListToolCallsQuery, IReadOnlyList<ToolCallView>>
{
    private readonly IToolCallAuditReader _auditReader;
    private readonly ICurrentTeam _currentTeam;

    public ListToolCallsQueryHandler(IToolCallAuditReader auditReader, ICurrentTeam currentTeam)
    {
        _auditReader = auditReader;
        _currentTeam = currentTeam;
    }

    public async Task<IReadOnlyList<ToolCallView>> Handle(ListToolCallsQuery request, CancellationToken cancellationToken) =>
        await _auditReader.ListForRunAsync(request.AgentRunId, _currentTeam.Id!.Value, cancellationToken).ConfigureAwait(false);
}
