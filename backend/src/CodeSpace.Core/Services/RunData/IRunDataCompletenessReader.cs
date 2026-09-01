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
        // One database statement, including the legacy arm. Splitting the status/header/member/manifest reads would
        // let first takeover or terminal sealing occur between queries and produce a view that never existed.
        var covered = from run in _db.WorkflowRun.AsNoTracking()
                          where run.Id == workflowRunId && run.TeamId == teamId
                          join coverage in _db.WorkflowRunDataCoverage.AsNoTracking()
                              on new { run.TeamId, WorkflowRunId = run.Id } equals new { coverage.TeamId, coverage.WorkflowRunId }
                          join member in _db.WorkflowRunDataCoverageFacet.AsNoTracking()
                              on new { run.TeamId, WorkflowRunId = run.Id } equals new { member.TeamId, member.WorkflowRunId } into members
                          from member in members.DefaultIfEmpty()
                          join statement in _db.WorkflowRunDataManifest.AsNoTracking()
                              on new { member.TeamId, member.WorkflowRunId, member.Facet } equals new { statement.TeamId, statement.WorkflowRunId, statement.Facet } into statements
                          from statement in statements.DefaultIfEmpty()
                          orderby member.Ordinal
                          select new CoverageRow
                          {
                              Status = run.Status,
                              CoverageState = coverage.State,
                              BaselineFacets = coverage.BaselineFacets,
                              MemberFacet = member == null ? null : member.Facet,
                              MemberOrdinal = member == null ? null : member.Ordinal,
                              StatementId = statement == null ? null : statement.Id,
                              ExpectedRecordCount = statement == null ? null : statement.ExpectedRecordCount,
                              PresentRecordCount = statement == null ? null : statement.PresentRecordCount,
                              KnownMissingCount = statement == null ? null : statement.KnownMissingCount,
                              Verdict = statement == null ? null : statement.Verdict,
                              Revision = statement == null ? null : statement.Revision,
                              SchemaVersion = statement == null ? null : statement.SchemaVersion,
                              LastModifiedAt = statement == null ? null : statement.LastModifiedAt,
                          };
        var legacy = from run in _db.WorkflowRun.AsNoTracking()
                     where run.Id == workflowRunId && run.TeamId == teamId
                        && !_db.WorkflowRunDataCoverage.Any(coverage => coverage.TeamId == run.TeamId && coverage.WorkflowRunId == run.Id)
                     join statement in _db.WorkflowRunDataManifest.AsNoTracking()
                         on new { run.TeamId, WorkflowRunId = run.Id } equals new { statement.TeamId, statement.WorkflowRunId } into statements
                     from statement in statements.DefaultIfEmpty()
                     select new CoverageRow
                     {
                         Status = run.Status,
                         CoverageState = null,
                         BaselineFacets = null,
                         MemberFacet = statement == null ? null : statement.Facet,
                         MemberOrdinal = null,
                         StatementId = statement == null ? null : statement.Id,
                         ExpectedRecordCount = statement == null ? null : statement.ExpectedRecordCount,
                         PresentRecordCount = statement == null ? null : statement.PresentRecordCount,
                         KnownMissingCount = statement == null ? null : statement.KnownMissingCount,
                         Verdict = statement == null ? null : statement.Verdict,
                         Revision = statement == null ? null : statement.Revision,
                         SchemaVersion = statement == null ? null : statement.SchemaVersion,
                         LastModifiedAt = statement == null ? null : statement.LastModifiedAt,
                     };
        var rows = await covered.Concat(legacy).OrderBy(row => row.MemberOrdinal).ThenBy(row => row.MemberFacet)
            .Take(MaxFacets + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0) return null;

        var truncated = rows.Count > MaxFacets;
        var bounded = rows.Take(MaxFacets).ToList();
        var persisted = rows[0].CoverageState is not null;
        var baseline = persisted ? rows[0].BaselineFacets! : RunDataManifestCoverage.LegacyV1Facets.ToArray();
        var required = baseline.Concat(bounded.Where(row => row.MemberFacet is not null
                && (persisted ? row.MemberOrdinal > baseline.Length : !baseline.Contains(row.MemberFacet, StringComparer.Ordinal)))
            .OrderBy(row => persisted ? row.MemberOrdinal : null).ThenBy(row => row.MemberFacet).Select(row => row.MemberFacet!)).ToList();
        var stated = bounded.Where(row => row.StatementId is not null).Select(row => row.MemberFacet!).ToHashSet(StringComparer.Ordinal);
        var missing = required.Where(facet => !stated.Contains(facet)).ToList();
        var facets = bounded.Where(row => row.StatementId is not null).Select(Project)
            .OrderBy(facet => facet.Facet, StringComparer.Ordinal).ToList();
        var terminal = rows[0].Status is WorkflowRunStatus.Success or WorkflowRunStatus.Failure or WorkflowRunStatus.Cancelled;
        var sealedCoverage = !persisted || rows[0].CoverageState == WorkflowRunDataCoverageStates.Sealed;

        return new WorkflowRunDataCompletenessView
        {
            RunId = workflowRunId,
            Scope = WorkflowRunDataCompletenessScope.RecordedFacetsOnly,
            Facets = facets,
            HasStatements = facets.Count > 0,
            IsTerminal = terminal,
            RequiredFacets = required,
            MissingFacetStatements = missing,
            RunWideVerdict = terminal && sealedCoverage && required.Count > 0 && !truncated && missing.Count == 0 ? Fold(facets) : null,
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

    private static WorkflowRunDataFacetCompleteness Project(CoverageRow statement) => new()
    {
        Facet = statement.MemberFacet!,
        ExpectedRecordCount = statement.ExpectedRecordCount,
        PresentRecordCount = statement.PresentRecordCount!.Value,
        KnownMissingCount = statement.KnownMissingCount!.Value,
        Verdict = statement.Verdict!.Value,
        IsStrictlyReadable = statement.Verdict.Value.IsStrictlyReadable(),
        Revision = statement.Revision!.Value,
        SchemaVersion = statement.SchemaVersion!.Value,
        LastModifiedAt = statement.LastModifiedAt!.Value,
    };

    private sealed class CoverageRow
    {
        public WorkflowRunStatus Status { get; init; }
        public string? CoverageState { get; init; }
        public string[]? BaselineFacets { get; init; }
        public string? MemberFacet { get; init; }
        public int? MemberOrdinal { get; init; }
        public Guid? StatementId { get; init; }
        public long? ExpectedRecordCount { get; init; }
        public long? PresentRecordCount { get; init; }
        public long? KnownMissingCount { get; init; }
        public WorkflowRunCaptureCompleteness? Verdict { get; init; }
        public long? Revision { get; init; }
        public int? SchemaVersion { get; init; }
        public DateTimeOffset? LastModifiedAt { get; init; }
    }
}
