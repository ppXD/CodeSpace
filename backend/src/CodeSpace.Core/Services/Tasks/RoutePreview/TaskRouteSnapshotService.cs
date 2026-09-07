using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Middlewares.Transactional;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Tasks.Effort;
using CodeSpace.Core.Services.Tasks.RoutePreview.Exceptions;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Commands.Tasks;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Tasks;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CodeSpace.Core.Services.Tasks.RoutePreview;

/// <summary>Persists routing once, then atomically binds that decision to one run across requests and workers. No model or network operation is performed under the consumption row lock.</summary>
public sealed class TaskRouteSnapshotService : ITaskRouteSnapshotService, IScopedDependency
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly IEffortRouter _router;
    private readonly TaskRoutePolicyFingerprint _policy;
    private readonly CodeSpaceDbContext _db;
    private readonly IPostCommitActions _postCommit;
    private PendingCommitRecovery? _pendingCommitRecovery;

    public TaskRouteSnapshotService(IEffortRouter router, TaskRoutePolicyFingerprint policy, CodeSpaceDbContext db, IPostCommitActions postCommit)
    {
        _router = router;
        _policy = policy;
        _db = db;
        _postCommit = postCommit;
    }

    public async Task<TaskRoutePreviewResult> CreateAsync(TaskLaunchRequest request, TaskLaunchSeed seed, CancellationToken cancellationToken)
    {
        await ResolvePendingCommitAsync().ConfigureAwait(false);
        var policy = _policy.Capture();
        var route = await _router.RouteAsync(TaskLaunchService.BuildRouteRequest(seed, request), cancellationToken).ConfigureAwait(false);
        if (policy != _policy.Capture()) throw new TaskRouteSnapshotMismatchException();

        var now = await ReadDatabaseClockAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = new TaskRouteSnapshot
        {
            Id = Guid.NewGuid(),
            TeamId = request.TeamId,
            ActorUserId = request.ActorUserId,
            InputDigest = InputDigest(request),
            SeedDigest = ContractHashing.Hash(seed, WorkflowJson.Options),
            PolicyFingerprint = policy,
            RouteJson = JsonSerializer.Serialize(route, WorkflowJson.Options),
            CreatedAt = now,
            ExpiresAt = now + Lifetime,
        };
        _db.TaskRouteSnapshot.Add(snapshot);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new TaskRoutePreviewResult { Route = route, DeploymentAutonomyCeiling = AgentAutonomyPolicy.DeploymentCeiling.ToString(), RouteSnapshotId = snapshot.Id, ExpiresAt = snapshot.ExpiresAt, CreatedAt = snapshot.CreatedAt };
    }

    public async Task<TaskRouteSnapshotDecision> ReadAsync(TaskLaunchRequest request, TaskLaunchSeed seed, CancellationToken cancellationToken)
    {
        await ResolvePendingCommitAsync().ConfigureAwait(false);
        var snapshot = await _db.TaskRouteSnapshot.AsNoTracking().SingleOrDefaultAsync(s => s.Id == request.RouteSnapshotId && s.TeamId == request.TeamId && s.ActorUserId == request.ActorUserId, cancellationToken).ConfigureAwait(false);
        return await ValidateAsync(snapshot, request, seed, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LaunchTaskResult> ConsumeAsync(TaskRouteSnapshotConsumption consumption, CancellationToken cancellationToken)
    {
        await ResolvePendingCommitAsync().ConfigureAwait(false);
        // The command middleware owns the production transaction. Direct callers need a checkpoint
        // covering COMMIT as well: deferred constraints can fail after staging/savepoints succeed.
        if (_db.Database.CurrentTransaction is not null) return await ConsumeWithSavepointAsync(consumption, cancellationToken).ConfigureAwait(false);

        var actions = _postCommit.CreateCheckpoint();
        var tracker = new TaskRouteChangeTrackerCheckpoint(_db);
        LaunchTaskResult? result = null;
        try
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            result = await ConsumeInTransactionAsync(consumption, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The owned transaction is disposed before recovery. Never drain an action whose commit
            // is uncertain. A committed Pending run remains recoverable by StuckRunReconcilerService.
            _postCommit.RollbackTo(actions);
            if (result is null) tracker.Restore();
            else
            {
                _pendingCommitRecovery = new PendingCommitRecovery(consumption.Request, result.RunId, tracker);
                try { await ResolvePendingCommitAsync().ConfigureAwait(false); }
                catch
                {
                    // Keep the original commit failure. If the database is still unreachable, every
                    // subsequent entry point resolves this checkpoint before it can route or stage again.
                }
            }
            throw;
        }
        await _postCommit.RunAllAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task ResolvePendingCommitAsync()
    {
        if (_pendingCommitRecovery is not { } pending) return;
        // A fresh connection and its own deadline also work after caller cancellation/connection loss.
        // FOR UPDATE waits for the earlier transaction's final outcome; an MVCC read of an unconsumed
        // version could otherwise mistake an in-flight COMMIT for an abort and replay caller inserts.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var connection = (NpgsqlConnection)((ICloneable)_db.Database.GetDbConnection()).Clone();
        await connection.OpenAsync(deadline.Token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT consumed_run_id FROM task_route_snapshot WHERE id = @id AND team_id = @team AND actor_user_id = @actor FOR UPDATE", connection);
        command.Parameters.AddWithValue("id", pending.Request.RouteSnapshotId!.Value);
        command.Parameters.AddWithValue("team", pending.Request.TeamId);
        command.Parameters.AddWithValue("actor", pending.Request.ActorUserId);
        var consumedRunId = await command.ExecuteScalarAsync(deadline.Token).ConfigureAwait(false);
        if (consumedRunId is null) throw new InvalidOperationException("The route consumption outcome cannot be resolved because its durable reference is no longer available.");
        if (consumedRunId is not Guid runId || runId != pending.RunId) pending.Tracker.Restore();
        _pendingCommitRecovery = null;
    }

    private sealed record PendingCommitRecovery(TaskLaunchRequest Request, Guid RunId, TaskRouteChangeTrackerCheckpoint Tracker);

    private async Task<LaunchTaskResult> ConsumeWithSavepointAsync(TaskRouteSnapshotConsumption consumption, CancellationToken cancellationToken)
    {
        var transaction = _db.Database.CurrentTransaction!;
        var savepoint = $"route_{Guid.NewGuid():N}";
        var actions = _postCommit.CreateCheckpoint();
        var tracker = new TaskRouteChangeTrackerCheckpoint(_db);
        await transaction.CreateSavepointAsync(savepoint, cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await ConsumeInTransactionAsync(consumption, cancellationToken).ConfigureAwait(false);
            await transaction.ReleaseSavepointAsync(savepoint, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            try { await transaction.RollbackToSavepointAsync(savepoint, CancellationToken.None).ConfigureAwait(false); }
            finally
            {
                _postCommit.RollbackTo(actions);
                tracker.Restore();
            }
            throw;
        }
    }

    private Task<DateTimeOffset> ReadDatabaseClockAsync(CancellationToken cancellationToken) => _db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(cancellationToken);

    private async Task<LaunchTaskResult> ConsumeInTransactionAsync(TaskRouteSnapshotConsumption consumption, CancellationToken cancellationToken)
    {
        var request = consumption.Request;
        // Never reuse a tracked preview row: another worker may have consumed it since this scope created it.
        var snapshot = await _db.TaskRouteSnapshot.FromSqlInterpolated($"SELECT * FROM task_route_snapshot WHERE id = {request.RouteSnapshotId} AND team_id = {request.TeamId} AND actor_user_id = {request.ActorUserId} FOR UPDATE").AsNoTracking().SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var decision = await ValidateAsync(snapshot, request, consumption.Seed, cancellationToken).ConfigureAwait(false);
        var result = decision.PreviousResult;
        if (result is null)
        {
            result = await consumption.StageAsync().ConfigureAwait(false);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var json = JsonSerializer.Serialize(result, WorkflowJson.Options);
            var written = await _db.TaskRouteSnapshot.Where(s => s.Id == snapshot!.Id && s.ConsumedRunId == null).ExecuteUpdateAsync(s => s.SetProperty(r => r.ConsumedRunId, result.RunId).SetProperty(r => r.ResultJson, json), cancellationToken).ConfigureAwait(false);
            if (written != 1) throw new InvalidOperationException("The locked routing decision could not be bound to its staged run.");
        }
        return result;
    }

    internal static string InputDigest(TaskLaunchRequest request) => ContractHashing.Hash(request with { RouteSnapshotId = null }, WorkflowJson.Options);

    private async Task<TaskRouteSnapshotDecision> ValidateAsync(TaskRouteSnapshot? snapshot, TaskLaunchRequest request, TaskLaunchSeed seed, CancellationToken cancellationToken)
    {
        if (snapshot is null) throw new KeyNotFoundException("Route preview not found or not accessible.");
        if (snapshot.InputDigest != InputDigest(request) || snapshot.SeedDigest != ContractHashing.Hash(seed, WorkflowJson.Options)) throw new TaskRouteSnapshotMismatchException();

        // Returning the previously committed result creates no new effect. An exact retry remains readable after
        // expiry/policy rotation, but only after fresh HTTP authority and launch scope guards and exact input checks.
        var previous = snapshot.ResultJson is { } json ? JsonSerializer.Deserialize<LaunchTaskResult>(json, WorkflowJson.Options) : null;
        if (previous is null && (snapshot.ExpiresAt <= await ReadDatabaseClockAsync(cancellationToken).ConfigureAwait(false) || snapshot.PolicyFingerprint != _policy.Capture())) throw new TaskRouteSnapshotMismatchException();
        var route = JsonSerializer.Deserialize<RoutePlan>(snapshot.RouteJson, WorkflowJson.Options) ?? throw new TaskRouteSnapshotMismatchException();
        return new TaskRouteSnapshotDecision(route, previous);
    }
}
