using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
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
        var status = await _db.WorkflowRun.AsNoTracking().Where(run => run.Id == workflowRunId && run.TeamId == teamId)
            .Select(run => (WorkflowRunStatus?)run.Status).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (status is null) return null;

        var statements = await _db.WorkflowRunDataManifest.AsNoTracking()
            .Where(statement => statement.WorkflowRunId == workflowRunId && statement.TeamId == teamId)
            .OrderBy(statement => statement.Facet).Take(MaxFacets + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var truncated = statements.Count > MaxFacets;
        var facets = statements.Take(MaxFacets).Select(Project).ToList();
        var present = facets.Select(facet => facet.Facet).ToHashSet(StringComparer.Ordinal);
        var missing = RunDataManifestCoverage.RequiredFacets.Where(facet => !present.Contains(facet)).ToList();
        var terminal = status is WorkflowRunStatus.Success or WorkflowRunStatus.Failure or WorkflowRunStatus.Cancelled;

        return new WorkflowRunDataCompletenessView
        {
            RunId = workflowRunId,
            Scope = WorkflowRunDataCompletenessScope.RecordedFacetsOnly,
            Facets = facets,
            HasStatements = facets.Count > 0,
            IsTerminal = terminal,
            RequiredFacets = RunDataManifestCoverage.RequiredFacets,
            MissingFacetStatements = missing,
            RunWideVerdict = terminal && !truncated && missing.Count == 0 ? Fold(facets) : null,
            Truncated = truncated,
        };
    }

    private static WorkflowRunCaptureCompleteness Fold(IReadOnlyList<WorkflowRunDataFacetCompleteness> facets)
    {
        if (facets.Any(facet => facet.Verdict == WorkflowRunCaptureCompleteness.Corrupt)) return WorkflowRunCaptureCompleteness.Corrupt;
        if (facets.Any(facet => facet.Verdict == WorkflowRunCaptureCompleteness.Unavailable)) return WorkflowRunCaptureCompleteness.Unavailable;
        if (facets.Any(facet => facet.Verdict == WorkflowRunCaptureCompleteness.Partial)) return WorkflowRunCaptureCompleteness.Partial;
        if (facets.Any(facet => facet.Verdict == WorkflowRunCaptureCompleteness.LegacyUnknown)) return WorkflowRunCaptureCompleteness.LegacyUnknown;
        return facets.Any(facet => facet.Verdict == WorkflowRunCaptureCompleteness.RedactedExact)
            ? WorkflowRunCaptureCompleteness.RedactedExact : WorkflowRunCaptureCompleteness.Exact;
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
