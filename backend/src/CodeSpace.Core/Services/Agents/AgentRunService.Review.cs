using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Review;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents;

public sealed partial class AgentRunService
{
    public async Task<AgentRun> CreateReviewAsync(AgentReviewCreation request, CancellationToken cancellationToken)
    {
        EnsureIndependentOwnershipTransaction();
        if (_db.ChangeTracker.HasChanges()) throw new InvalidOperationException("Reviewer admission requires a clean scope; unrelated caller changes must not be committed by delegation.");
        await _admissionController.EnsureAgentRunAdmittedAsync(request.TeamId, cancellationToken).ConfigureAwait(false);
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // Retain this row lock through child persistence. No model, process launch or remote I/O belongs inside it.
        await AssertOwnershipAsync(request.ParentOwner, cancellationToken).ConfigureAwait(false);
        var parent = await _db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == request.ParentOwner.RunId, cancellationToken).ConfigureAwait(false);
        var childId = Guid.NewGuid();
        var task = await _authority.AdmitReviewAsync(new(request.Task, request.TeamId, childId, parent.WorkflowRunId), parent, cancellationToken).ConfigureAwait(false);
        var child = await PersistCreatedAsync(new AgentRunCreation { Task = task, TeamId = request.TeamId, RunId = childId, WorkflowRunId = parent.WorkflowRunId, NodeId = parent.NodeId, IterationKey = AgentOutputReviewer.ReviewIterationKey(parent.IterationKey) }, cancellationToken).ConfigureAwait(false);
        await AssertOwnershipAsync(request.ParentOwner, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return child;
    }
}
