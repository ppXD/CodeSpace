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

namespace CodeSpace.Core.Services.Tasks.RoutePreview;

/// <summary>Persists routing once, then atomically binds that decision to one run across requests and workers. No model or network operation is performed under the consumption row lock.</summary>
public sealed class TaskRouteSnapshotService : ITaskRouteSnapshotService, IScopedDependency
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly IEffortRouter _router;
    private readonly TaskRoutePolicyFingerprint _policy;
    private readonly CodeSpaceDbContext _db;
    private readonly IPostCommitActions _postCommit;

    public TaskRouteSnapshotService(IEffortRouter router, TaskRoutePolicyFingerprint policy, CodeSpaceDbContext db, IPostCommitActions postCommit)
    {
        _router = router;
        _policy = policy;
        _db = db;
        _postCommit = postCommit;
    }

    public async Task<TaskRoutePreviewResult> CreateAsync(TaskLaunchRequest request, TaskLaunchSeed seed, CancellationToken cancellationToken)
    {
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
        var snapshot = await _db.TaskRouteSnapshot.AsNoTracking().SingleOrDefaultAsync(s => s.Id == request.RouteSnapshotId && s.TeamId == request.TeamId && s.ActorUserId == request.ActorUserId, cancellationToken).ConfigureAwait(false);
        return await ValidateAsync(snapshot, request, seed, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LaunchTaskResult> ConsumeAsync(TaskRouteSnapshotConsumption consumption, CancellationToken cancellationToken)
    {
        // The command middleware owns the production transaction. Direct service callers receive the same atomicity,
        // including delayed dispatch; rollback leaves the preview reusable after cancellation or process loss.
        if (_db.Database.CurrentTransaction is not null) return await ConsumeWithSavepointAsync(consumption, cancellationToken).ConfigureAwait(false);

        LaunchTaskResult result;
        await using (var transaction = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            result = await ConsumeWithSavepointAsync(consumption, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        await _postCommit.RunAllAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

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
