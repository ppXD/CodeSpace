using System.Collections.Concurrent;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Display;

/// <summary>
/// Request-scoped, observation-only access to the exact team/run identity needed by polling projections. The Activity
/// timeline and phase tree independently check tenancy and then envelope their result with the same mutable status;
/// this bundle shares that one narrow read inside the request without caching status across requests.
/// </summary>
public interface IWorkflowRunObservationIdentityBundle
{
    Task<WorkflowRunObservationIdentity?> GetAsync(Guid teamId, Guid runId, CancellationToken cancellationToken);
}

/// <summary>The bounded identity/status row. It carries no graph, payload, output, wait, record or artifact bytes.</summary>
public sealed record WorkflowRunObservationIdentity(Guid RunId, long RunNumber, WorkflowRunStatus Status);

public sealed class WorkflowRunObservationIdentityBundle : IWorkflowRunObservationIdentityBundle, IScopedDependency, IDisposable, IAsyncDisposable
{
    private readonly CodeSpaceDbContext _db;
    private readonly CancellationToken? _httpRequestToken;
    private readonly CancellationTokenSource _scopeEnded = new();
    private readonly ConcurrentDictionary<ObservationKey, Lazy<Task<WorkflowRunObservationIdentity?>>> _loads = new();
    private int _disposed;

    public WorkflowRunObservationIdentityBundle(CodeSpaceDbContext db, IHttpContextAccessor httpContextAccessor)
    {
        _db = db;
        _httpRequestToken = httpContextAccessor.HttpContext?.RequestAborted;
    }

    public async Task<WorkflowRunObservationIdentity?> GetAsync(Guid teamId, Guid runId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var key = new ObservationKey(teamId, runId);
        var load = _loads.GetOrAdd(key, _ => new Lazy<Task<WorkflowRunObservationIdentity?>>(
            () => LoadAsync(key, cancellationToken), LazyThreadSafetyMode.ExecutionAndPublication));

        return await load.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        CancelScope();
        ObserveSynchronously(CreatedLoads());
        _scopeEnded.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        CancelScope();
        await ObserveAsync(CreatedLoads()).ConfigureAwait(false);
        _scopeEnded.Dispose();
    }

    private async Task<WorkflowRunObservationIdentity?> LoadAsync(ObservationKey key, CancellationToken firstConsumerToken)
    {
        var ownerToken = _httpRequestToken ?? firstConsumerToken;
        using var linked = ownerToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(_scopeEnded.Token, ownerToken)
            : CancellationTokenSource.CreateLinkedTokenSource(_scopeEnded.Token);

        return await _db.WorkflowRun.AsNoTracking()
            .Where(run => run.TeamId == key.TeamId && run.Id == key.RunId)
            .Select(run => new WorkflowRunObservationIdentity(run.Id, run.RunNumber, run.Status))
            .SingleOrDefaultAsync(linked.Token).ConfigureAwait(false);
    }

    private void CancelScope()
    {
        try { _scopeEnded.Cancel(); }
        catch (AggregateException) { }
    }

    private Task[] CreatedLoads() => _loads.Values.Where(load => load.IsValueCreated).Select(load => load.Value).ToArray();

    private static void ObserveSynchronously(IEnumerable<Task> loads)
    {
        foreach (var load in loads)
            try { load.GetAwaiter().GetResult(); }
            catch (Exception) { }
    }

    private static async Task ObserveAsync(IEnumerable<Task> loads)
    {
        foreach (var load in loads)
            try { await load.ConfigureAwait(false); }
            catch (Exception) { }
    }

    private readonly record struct ObservationKey(Guid TeamId, Guid RunId);
}
