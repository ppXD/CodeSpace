using System.Globalization;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Queries.Workflows;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Handlers.QueryHandlers.Workflows;

/// <summary>Metadata-only run-ref lookup. The projection deliberately cannot materialize the graph, cells, outputs or artifacts.</summary>
public sealed class GetWorkflowRunIdentityByRefQueryHandler : IRequestHandler<GetWorkflowRunIdentityByRefQuery, WorkflowRunIdentity?>
{
    private readonly CodeSpaceDbContext _db;
    private readonly ICurrentTeam _currentTeam;

    public GetWorkflowRunIdentityByRefQueryHandler(CodeSpaceDbContext db, ICurrentTeam currentTeam)
    {
        _db = db;
        _currentTeam = currentTeam;
    }

    public async Task<WorkflowRunIdentity?> Handle(GetWorkflowRunIdentityByRefQuery request, CancellationToken cancellationToken)
    {
        var query = BuildQuery(request.IdOrNumber);
        return query == null ? null : await query.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    internal IQueryable<WorkflowRunIdentity>? BuildQuery(string idOrNumber)
    {
        var teamId = _currentTeam.Id!.Value;
        var runs = _db.WorkflowRun.AsNoTracking().Where(run => run.TeamId == teamId);

        if (Guid.TryParse(idOrNumber, out var id)) runs = runs.Where(run => run.Id == id);
        else if (long.TryParse(idOrNumber, NumberStyles.None, CultureInfo.InvariantCulture, out var runNumber) && runNumber > 0) runs = runs.Where(run => run.RunNumber == runNumber);
        else return null;

        return runs.Select(run => new WorkflowRunIdentity { Id = run.Id, RunNumber = run.RunNumber, Status = run.Status });
    }
}
