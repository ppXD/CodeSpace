using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Learning;

/// <summary>The immutable, run-wide treatment receipt shared by every agent dispatch in an arbitrary workflow graph.</summary>
public sealed record RunLessonAssignment(Guid WorkflowRunId, Guid TeamId, string Arm, IReadOnlyList<Guid> LessonIds);

/// <summary>A proposed first assignment. Concurrent proposals race at the database unique key; every caller reads the winner.</summary>
public sealed record RunLessonAssignmentProposal(Guid WorkflowRunId, Guid TeamId, string Arm, IReadOnlyList<Guid> LessonIds);

public interface IRunLessonAssignmentStore
{
    Task<RunLessonAssignment?> ReadAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken);
    Task<RunLessonAssignment> GetOrCreateAsync(RunLessonAssignmentProposal proposal, CancellationToken cancellationToken);
}

public sealed class RunLessonAssignmentStore : IRunLessonAssignmentStore, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;

    public RunLessonAssignmentStore(CodeSpaceDbContext db) => _db = db;

    public async Task<RunLessonAssignment?> ReadAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken)
    {
        var row = await _db.Database.SqlQuery<AssignmentRow>($"""
            SELECT workflow_run_id, team_id, arm, lesson_ids
            FROM workflow_run_lesson_assignment
            WHERE workflow_run_id = {workflowRunId} AND team_id = {teamId}
            """).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return row is null ? null : Materialize(row);
    }

    public async Task<RunLessonAssignment> GetOrCreateAsync(RunLessonAssignmentProposal proposal, CancellationToken cancellationToken)
    {
        if (!LessonArms.IsKnown(proposal.Arm)) throw new ArgumentOutOfRangeException(nameof(proposal), proposal.Arm, "Unknown lesson assignment arm.");

        var lessonIds = proposal.Arm == LessonArms.Injected ? proposal.LessonIds.Distinct().ToArray() : [];
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO workflow_run_lesson_assignment (workflow_run_id, team_id, arm, lesson_ids, assigned_at)
            SELECT {proposal.WorkflowRunId}, {proposal.TeamId}, {proposal.Arm}, {lessonIds}, clock_timestamp()
            FROM workflow_run
            WHERE id = {proposal.WorkflowRunId} AND team_id = {proposal.TeamId}
            ON CONFLICT (workflow_run_id) DO NOTHING
            """, cancellationToken).ConfigureAwait(false);

        return await ReadAsync(proposal.WorkflowRunId, proposal.TeamId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The workflow run is unavailable in the requested team scope.");
    }

    private static RunLessonAssignment Materialize(AssignmentRow row)
    {
        if (!LessonArms.IsKnown(row.Arm)) throw new InvalidOperationException($"Workflow run {row.WorkflowRunId} has an unknown lesson assignment arm.");
        return new(row.WorkflowRunId, row.TeamId, row.Arm, row.LessonIds);
    }

    private sealed record AssignmentRow(Guid WorkflowRunId, Guid TeamId, string Arm, Guid[] LessonIds);
}
