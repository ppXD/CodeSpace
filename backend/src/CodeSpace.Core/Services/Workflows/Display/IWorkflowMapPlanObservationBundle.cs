using System.Collections.Concurrent;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Dtos.Workflows;
using Microsoft.AspNetCore.Http;

namespace CodeSpace.Core.Services.Workflows.Display;

/// <summary>Request-scoped sharing of the one bounded Map plan read used by Activity and Journal.</summary>
public interface IWorkflowMapPlanObservationBundle
{
    Task<WorkflowMapPlanObservation?> GetAsync(Guid runId, Guid teamId, CancellationToken cancellationToken);
}

public sealed class WorkflowMapPlanObservationBundle : IWorkflowMapPlanObservationBundle, IScopedDependency, IDisposable, IAsyncDisposable
{
    private readonly IWorkflowMapPlanObservationReader _reader;
    private readonly CancellationToken? _httpRequestToken;
    private readonly CancellationTokenSource _scopeEnded = new();
    private readonly ConcurrentDictionary<ObservationKey, Lazy<Task<WorkflowMapPlanObservation?>>> _loads = new();
    private int _disposed;

    public WorkflowMapPlanObservationBundle(IWorkflowMapPlanObservationReader reader, IHttpContextAccessor httpContextAccessor)
    {
        _reader = reader;
        _httpRequestToken = httpContextAccessor.HttpContext?.RequestAborted;
    }

    public async Task<WorkflowMapPlanObservation?> GetAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var key = new ObservationKey(teamId, runId);
        var load = _loads.GetOrAdd(key, _ => new Lazy<Task<WorkflowMapPlanObservation?>>(
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

    private async Task<WorkflowMapPlanObservation?> LoadAsync(ObservationKey key, CancellationToken firstConsumerToken)
    {
        var ownerToken = _httpRequestToken ?? firstConsumerToken;
        using var linked = ownerToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(_scopeEnded.Token, ownerToken)
            : CancellationTokenSource.CreateLinkedTokenSource(_scopeEnded.Token);
        var result = await _reader.ReadAsync(new WorkflowMapPlanObservationRequest(key.RunId, key.TeamId, WorkflowRunViewScope.LineageMerged), linked.Token).ConfigureAwait(false);
        if (result is not null && result.RunId != key.RunId) throw new InvalidOperationException("The bounded Map plan observation reader returned a contradictory run identity.");
        return result;
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
