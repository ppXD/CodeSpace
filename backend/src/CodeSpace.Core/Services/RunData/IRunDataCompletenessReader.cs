using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Dtos.Workflows;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.RunData;

/// <summary>
/// Observation-only read side of the Workflow Run data manifest. It reports bounded producer statements and never
/// folds absent statements into a run-wide verdict. No execution, terminal, planner, oracle or completion path consumes
/// this seam.
/// </summary>
public interface IRunDataCompletenessReader
{
    Task<WorkflowRunDataCompletenessView?> ReadAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IRunDataCompletenessReader"/>
public sealed class RunDataCompletenessReader : IRunDataCompletenessReader, IScopedDependency
{
    internal const int MaxFacets = 100;

    private readonly CodeSpaceDbContext _db;

    public RunDataCompletenessReader(CodeSpaceDbContext db) { _db = db; }

    public async Task<WorkflowRunDataCompletenessView?> ReadAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken)
    {
        var rows = await (
            from run in _db.WorkflowRun.AsNoTracking()
            where run.Id == workflowRunId && run.TeamId == teamId
            join statement in _db.WorkflowRunDataManifest.AsNoTracking()
                on new { TeamId = run.TeamId, WorkflowRunId = run.Id }
                equals new { statement.TeamId, statement.WorkflowRunId }
                into statementGroup
            from statement in statementGroup.DefaultIfEmpty()
            orderby statement.Facet
            select statement)
            .Take(MaxFacets + 1)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (rows.Count == 0) return null;

        var statements = rows.Where(statement => statement != null).ToList();
        var truncated = statements.Count > MaxFacets;
        var facets = statements.Take(MaxFacets).Select(Project).ToList();

        return new WorkflowRunDataCompletenessView
        {
            RunId = workflowRunId,
            Scope = WorkflowRunDataCompletenessScope.RecordedFacetsOnly,
            Facets = facets,
            HasStatements = facets.Count > 0,
            RunWideVerdict = null,
            Truncated = truncated,
        };
    }

    private static WorkflowRunDataFacetCompleteness Project(WorkflowRunDataManifest statement) => new()
    {
        Facet = statement.Facet,
        ExpectedRecordCount = statement.ExpectedRecordCount,
        PresentRecordCount = statement.PresentRecordCount,
        KnownMissingCount = statement.KnownMissingCount,
        Verdict = statement.Verdict,
        IsStrictlyReadable = statement.Verdict.IsStrictlyReadable(),
        Revision = statement.Revision,
        SchemaVersion = statement.SchemaVersion,
        LastModifiedAt = statement.LastModifiedAt,
    };
}
