using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Queries.Agents;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Agents;

public sealed class ListToolCallsQueryHandler : IRequestHandler<ListToolCallsQuery, IReadOnlyList<ToolCallView>>
{
    private readonly IToolCallLedgerService _ledger;
    private readonly ICurrentTeam _currentTeam;

    public ListToolCallsQueryHandler(IToolCallLedgerService ledger, ICurrentTeam currentTeam)
    {
        _ledger = ledger;
        _currentTeam = currentTeam;
    }

    public async Task<IReadOnlyList<ToolCallView>> Handle(ListToolCallsQuery request, CancellationToken cancellationToken)
    {
        var rows = await _ledger.GetForRunAsync(request.AgentRunId, _currentTeam.Id!.Value, cancellationToken).ConfigureAwait(false);

        return rows.OrderBy(r => r.CreatedDate).Select(r => new ToolCallView
        {
            ToolKind = r.ToolKind,
            Status = r.Status,
            CreatedDate = r.CreatedDate,
            LastModifiedDate = r.LastModifiedDate,
            Error = r.Error,
            ApprovedByUserId = r.ApprovedByUserId,
            ApprovedAt = r.ApprovedAt,
        }).ToList();
    }
}
