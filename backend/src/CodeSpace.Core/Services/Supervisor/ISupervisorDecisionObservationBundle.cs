using System.Collections.Concurrent;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Entities;
using Microsoft.AspNetCore.Http;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// Request-scoped, observation-only access to a supervisor decision tape. Timeline, Room and Journal projections often
/// fold the same tape independently; this seam shares their one in-flight read and its successful result for the exact
/// tenant/run key. It is deliberately NOT an execution or authority seam: rehydrate, decision and mutation paths keep
/// reading <see cref="ISupervisorDecisionLog"/> directly.
///
/// <para>This first cost slice still loads every payload/outcome byte for the run. It removes duplicate SQL/transfer/parse
/// inside one request; a later bounded-leaf/page projection is still required for long-run scaling.</para>
/// </summary>
public interface ISupervisorDecisionObservationBundle
{
    Task<IReadOnlyList<SupervisorDecisionRecord>> GetForRunAsync(Guid supervisorRunId, Guid teamId, CancellationToken cancellationToken);
}

/// <summary>
/// One instance per DI lifetime scope. An HTTP load belongs to <see cref="HttpContext.RequestAborted"/>, while each
/// consumer token only cancels that consumer's wait. Outside HTTP, the first consumer token owns the shared load because
/// there is no wider request token; bundle disposal always cancels and observes outstanding work before its scoped
/// <c>DbContext</c> can be disposed.
/// </summary>
public sealed class SupervisorDecisionObservationBundle : ISupervisorDecisionObservationBundle, IScopedDependency, IDisposable, IAsyncDisposable
{
    private readonly ISupervisorDecisionLog _ledger;
    private readonly CancellationToken? _httpRequestToken;
    private readonly CancellationTokenSource _scopeEnded = new();
    private readonly ConcurrentDictionary<ObservationKey, Lazy<Task<IReadOnlyList<SupervisorDecisionRecord>>>> _loads = new();
    private int _disposed;

    public SupervisorDecisionObservationBundle(ISupervisorDecisionLog ledger, IHttpContextAccessor httpContextAccessor)
    {
        _ledger = ledger;
        _httpRequestToken = httpContextAccessor.HttpContext?.RequestAborted;
    }

    public async Task<IReadOnlyList<SupervisorDecisionRecord>> GetForRunAsync(Guid supervisorRunId, Guid teamId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var key = new ObservationKey(teamId, supervisorRunId);
        var lazy = _loads.GetOrAdd(key, _ => new Lazy<Task<IReadOnlyList<SupervisorDecisionRecord>>>(() => LoadAsync(key, cancellationToken), LazyThreadSafetyMode.ExecutionAndPublication));
        var shared = lazy.Value;

        return await shared.WaitAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task<IReadOnlyList<SupervisorDecisionRecord>> LoadAsync(ObservationKey key, CancellationToken firstConsumerToken)
    {
        var ownerToken = _httpRequestToken ?? firstConsumerToken;
        using var linked = ownerToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(_scopeEnded.Token, ownerToken)
            : CancellationTokenSource.CreateLinkedTokenSource(_scopeEnded.Token);

        return await _ledger.GetForRunAsync(key.SupervisorRunId, key.TeamId, linked.Token).ConfigureAwait(false);
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

    private readonly record struct ObservationKey(Guid TeamId, Guid SupervisorRunId);
}
