using System.Collections.Concurrent;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Workflows.Supervisor;
using Microsoft.AspNetCore.Http;

namespace CodeSpace.Core.Services.Supervisor.Observation;

/// <summary>
/// Request-scoped observation-only access to one bounded Tail page of Plan leaves. The two journal Plan fact sources
/// share one in-flight exact-team/run read; the bundle never follows Older and therefore has a hard 500-row ceiling.
/// </summary>
public interface ISupervisorPlanObservationPageBundle
{
    Task<SupervisorPlanObservationPage?> GetForRunAsync(Guid supervisorRunId, Guid teamId, CancellationToken cancellationToken);
}

public sealed class SupervisorPlanObservationPageBundle : ISupervisorPlanObservationPageBundle, IScopedDependency, IDisposable, IAsyncDisposable
{
    public const int PageLimit = SupervisorDecisionObservationPageLimits.MaximumLimit;

    private readonly ISupervisorPlanObservationLeafReader _reader;
    private readonly CancellationToken? _httpRequestToken;
    private readonly CancellationTokenSource _scopeEnded = new();
    private readonly ConcurrentDictionary<ObservationKey, Lazy<Task<SupervisorPlanObservationPage?>>> _loads = new();
    private int _disposed;

    public SupervisorPlanObservationPageBundle(ISupervisorPlanObservationLeafReader reader, IHttpContextAccessor httpContextAccessor)
    {
        _reader = reader;
        _httpRequestToken = httpContextAccessor.HttpContext?.RequestAborted;
    }

    public async Task<SupervisorPlanObservationPage?> GetForRunAsync(Guid supervisorRunId, Guid teamId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var key = new ObservationKey(teamId, supervisorRunId);
        var lazy = _loads.GetOrAdd(key, _ => new Lazy<Task<SupervisorPlanObservationPage?>>(
            () => LoadAsync(key, cancellationToken), LazyThreadSafetyMode.ExecutionAndPublication));
        return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task<SupervisorPlanObservationPage?> LoadAsync(ObservationKey key, CancellationToken firstConsumerToken)
    {
        var ownerToken = _httpRequestToken ?? firstConsumerToken;
        using var linked = ownerToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(_scopeEnded.Token, ownerToken)
            : CancellationTokenSource.CreateLinkedTokenSource(_scopeEnded.Token);
        var page = await _reader.ReadPageAsync(new SupervisorPlanObservationPageRequest(
            key.TeamId, key.SupervisorRunId, SupervisorDecisionObservationStoryPageMode.Tail, Limit: PageLimit), linked.Token).ConfigureAwait(false);
        if (page is not null) ValidatePage(key, page);
        return page;
    }

    private static void ValidatePage(ObservationKey key, SupervisorPlanObservationPage page)
    {
        var valid = page.SupervisorRunId == key.SupervisorRunId
            && page.Mode == SupervisorDecisionObservationStoryPageMode.Tail.ToString()
            && page.RequestCursor is null
            && page.Limit == PageLimit
            && page.Items.Count <= PageLimit
            && (!page.HasMore || page.Items.Count == PageLimit);
        long prior = 0;
        var ids = new HashSet<Guid>();
        foreach (var item in page.Items)
        {
            valid &= item.Metadata.SupervisorRunId == key.SupervisorRunId
                && item.Metadata.DecisionId != Guid.Empty
                && item.Metadata.DecisionKind == SupervisorDecisionKinds.Plan
                && item.Metadata.StoryOrder > prior
                && item.Metadata.ObservationRevision > 0
                && item.Metadata.ErrorTotalBytes >= 0
                && ids.Add(item.Metadata.DecisionId)
                && Enum.IsDefined(item.Metadata.Status)
                && Enum.IsDefined(item.SubtasksState)
                && Enum.IsDefined(item.ModelUsageState)
                && item.SubtasksTotalCount >= 0
                && item.SubtasksOmittedCount >= 0
                && item.Subtasks.Count <= SupervisorPlanObservationLeafLimits.MaximumSubtasks;
            prior = item.Metadata.StoryOrder;
        }
        if (!valid) throw new InvalidOperationException("The bounded supervisor Plan observation reader returned a contradictory page.");
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
