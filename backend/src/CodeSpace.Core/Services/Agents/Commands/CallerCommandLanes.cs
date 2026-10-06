using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Agents.Commands;

/// <summary>
/// One <c>agent.run_command</c> at a time per calling agent run. Each command gets a cgroup leaf of its own, beside the
/// agent's leaf rather than inside it, carrying the run's whole tier row (<c>RunCommandService.BuildSpec</c>). The run
/// token in the agent's config lets it open as many endpoint connections as it likes, so commands it started at once
/// would each hold a full row. Queued here, the commands one run has running never hold more than one row between them.
/// The agent's own leaf is separate, so an agent and its one running command can together hold up to two rows.
///
/// <para>Process-local by design: a run's MCP endpoint, and so every command its agent asks for, lives in the worker
/// that launched it. A lane exists only while a command of its run holds or awaits it.</para>
/// </summary>
public sealed class CallerCommandLanes : ISingletonDependency
{
    private readonly Dictionary<Guid, Lane> _lanes = new();
    private readonly object _gate = new();

    /// <summary>How many runs currently hold or await a lane — what a test reads to prove a finished run leaves nothing behind.</summary>
    internal int Count
    {
        get { lock (_gate) return _lanes.Count; }
    }

    /// <summary>Wait for <paramref name="runId"/>'s lane and hold it until the returned handle is disposed. A cancelled wait gives its place back.</summary>
    public async Task<IAsyncDisposable> EnterAsync(Guid runId, CancellationToken cancellationToken)
    {
        var lane = Join(runId);

        try
        {
            await lane.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Leave(runId, lane, held: false);
            throw;
        }

        return new Held(this, runId, lane);
    }

    private Lane Join(Guid runId)
    {
        lock (_gate)
        {
            if (!_lanes.TryGetValue(runId, out var lane)) _lanes[runId] = lane = new Lane();

            lane.Users++;

            return lane;
        }
    }

    private void Leave(Guid runId, Lane lane, bool held)
    {
        if (held) lane.Semaphore.Release();

        lock (_gate)
        {
            if (--lane.Users > 0) return;

            _lanes.Remove(runId);
            lane.Semaphore.Dispose();
        }
    }

    private sealed class Lane
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int Users { get; set; }
    }

    private sealed class Held(CallerCommandLanes lanes, Guid runId, Lane lane) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) lanes.Leave(runId, lane, held: true);

            return ValueTask.CompletedTask;
        }
    }
}
