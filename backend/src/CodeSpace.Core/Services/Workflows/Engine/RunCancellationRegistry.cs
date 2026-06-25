using System.Collections.Concurrent;
using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Workflows.Engine;

/// <summary>
/// The in-process bridge that makes an operator cancel COOPERATIVE for a run whose engine walk is executing
/// right now on THIS host. The engine job is enqueued with <c>CancellationToken.None</c> (Hangfire activation
/// carries no job-cancellation token), so without this the walk keeps firing side-effecting nodes after the row
/// is flipped Cancelled. While a walk runs, <see cref="WorkflowEngine"/> registers a per-run linked token and
/// threads it through the walk; <c>WorkflowService.CancelRunAsync</c> calls <see cref="Cancel"/> after the
/// status flip, tripping that token so the walk stops at its next safe checkpoint and unwinds through the
/// engine's <c>OperationCanceledException</c> handler.
///
/// <para>SINGLE-HOST scope by design: the registry lives in one process, so it cancels only a walk on the SAME
/// replica as the cancel request. A cancel that lands on another replica is honoured by the engine's
/// wave-boundary status re-read (it sees the persisted Cancelled status and stops) — this registry is the fast
/// same-host path, that re-read is the cross-host backstop. A walk that finishes normally disposes its
/// registration; a stale entry left by a crashed walk is replaced last-writer-wins on the next claim and is
/// harmless (its run is already terminal, so a late <see cref="Cancel"/> is a no-op).</para>
/// </summary>
public interface IRunCancellationRegistry
{
    /// <summary>Register a per-run cancellation source linked to <paramref name="linkedToken"/> for the duration of a walk; the returned registration's <see cref="IRunCancellationRegistration.Token"/> is threaded through the walk, and disposing it unregisters. A prior registration for the same run is replaced + disposed (last-writer-wins).</summary>
    IRunCancellationRegistration Register(Guid runId, CancellationToken linkedToken);

    /// <summary>Trip the registered token for <paramref name="runId"/> if a walk is running on this host — a no-op when none is registered (already finished, or running on another replica).</summary>
    void Cancel(Guid runId);
}

/// <summary>A live registration: carries the walk's cancellation token; disposing it removes the run from the registry.</summary>
public interface IRunCancellationRegistration : IDisposable
{
    CancellationToken Token { get; }
}

public sealed class RunCancellationRegistry : IRunCancellationRegistry, ISingletonDependency
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _sources = new();

    public IRunCancellationRegistration Register(Guid runId, CancellationToken linkedToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(linkedToken);

        // Last-writer-wins: dispose any stale source a crashed prior walk failed to unregister. The claim CAS
        // makes concurrent walks of the same run impossible, so this only ever reaps a dead entry.
        if (_sources.TryRemove(runId, out var stale)) stale.Dispose();

        _sources[runId] = cts;
        return new Registration(this, runId, cts);
    }

    public void Cancel(Guid runId)
    {
        if (!_sources.TryGetValue(runId, out var cts)) return;

        try { cts.Cancel(); }
        catch (ObjectDisposedException) { /* the walk finished + disposed between the lookup and the cancel — already terminal */ }
    }

    private void Unregister(Guid runId, CancellationTokenSource cts)
    {
        // Only remove OUR source — a newer registration (last-writer-wins) must not be evicted by an older walk's dispose.
        if (_sources.TryGetValue(runId, out var current) && ReferenceEquals(current, cts))
            _sources.TryRemove(runId, out _);

        cts.Dispose();
    }

    private sealed class Registration : IRunCancellationRegistration
    {
        private readonly RunCancellationRegistry _registry;
        private readonly Guid _runId;
        private readonly CancellationTokenSource _cts;

        public Registration(RunCancellationRegistry registry, Guid runId, CancellationTokenSource cts)
        {
            _registry = registry;
            _runId = runId;
            _cts = cts;
        }

        public CancellationToken Token => _cts.Token;

        public void Dispose() => _registry.Unregister(_runId, _cts);
    }
}
