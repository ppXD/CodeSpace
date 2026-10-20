using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows.ExternalTriggers;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Queries.Workflows;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Handlers.QueryHandlers.Workflows;

public sealed class ListWorkflowExternalTriggersQueryHandler(CodeSpaceDbContext db, IExternalTriggerStore store, ICurrentTeam team)
    : IRequestHandler<ListWorkflowExternalTriggersQuery, WorkflowExternalTriggersResult?>
{
    public async Task<WorkflowExternalTriggersResult?> Handle(ListWorkflowExternalTriggersQuery request, CancellationToken cancellationToken)
    {
        var teamId = team.Id!.Value;
        if (!await db.Workflow.AnyAsync(w => w.Id == request.WorkflowId && w.TeamId == teamId && w.DeletedDate == null, cancellationToken).ConfigureAwait(false))
            return null;

        var triggers = await store.ListForWorkflowAsync(request.WorkflowId, teamId, cancellationToken).ConfigureAwait(false);
        return new WorkflowExternalTriggersResult(triggers
            .Select(t => new WorkflowExternalTriggerSummary(t.Id, t.WorkflowId, t.NodeId, t.Slug)).ToArray());
    }
}
