using System.Collections.Concurrent;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Dtos.Sessions.Room;

namespace CodeSpace.Core.Services.Sessions;

/// <summary>
/// An in-process cache of the HEAVY per-turn projections (a Room <see cref="AssistantTurnBlock"/>, a Journal step
/// list) for TERMINAL turns. Projecting every turn richly (so each turn's full execution UI is available on expand)
/// costs ~28 DB reads/turn for the Room and ~45 for the Journal; the Room/Journal poll re-runs the whole projection
/// every 2s while any turn is live, re-reading immutable PAST turns each time. A terminal turn's flow never changes
/// (a rerun mints a NEW run id → a new attempt → a cache miss), so its projection is safe to compute once and reuse.
///
/// <para>Keyed by the resolved focus run id (the attempt actually shown). Singleton by design — the projectors are
/// scoped (per request), so a per-request cache would never survive the next poll; only a singleton spans polls. A
/// rerun is naturally safe (new run id ⇒ miss); the one mutation of an already-terminal Room block is opening a PR on
/// it (the publish/delivery layer changes), so <see cref="IRoomPullRequestService"/> evicts that run on open. Journal
/// steps are pure ledger projection with no such caveat. Bounded (LRU-ish) so it can't grow unbounded across runs.</para>
/// </summary>
public interface ISessionTurnCache
{
    /// <summary>Return the cached Room block for a TERMINAL turn's run, or compute + cache it. Non-terminal callers must NOT use this (a live turn's projection still changes).</summary>
    Task<AssistantTurnBlock> GetOrAddRoomAsync(Guid runId, Func<Task<AssistantTurnBlock>> factory);

    /// <summary>Return the cached Journal steps for a TERMINAL turn's run, or compute + cache them.</summary>
    Task<IReadOnlyList<JournalStep>> GetOrAddJournalAsync(Guid runId, Func<Task<IReadOnlyList<JournalStep>>> factory);

    /// <summary>Drop a run's cached projections — called when a terminal run's Room block mutates (a PR is opened on it).</summary>
    void Evict(Guid runId);
}

public sealed class SessionTurnCache : ISessionTurnCache, ISingletonDependency
{
    // Cap the distinct runs cached; past this we drop the oldest-touched entries. A session rarely has more than a few
    // dozen terminal turns, so this comfortably holds several concurrently-viewed sessions before any eviction bites.
    private const int MaxEntries = 512;

    private readonly ConcurrentDictionary<Guid, AssistantTurnBlock> _room = new();
    private readonly ConcurrentDictionary<Guid, IReadOnlyList<JournalStep>> _journal = new();
    // Monotonic touch order for a coarse LRU — the value is a tick stamp bumped on every read/write of a key.
    private readonly ConcurrentDictionary<Guid, long> _touch = new();
    private long _clock;

    public Task<AssistantTurnBlock> GetOrAddRoomAsync(Guid runId, Func<Task<AssistantTurnBlock>> factory) => GetOrAddAsync(_room, runId, factory);

    public Task<IReadOnlyList<JournalStep>> GetOrAddJournalAsync(Guid runId, Func<Task<IReadOnlyList<JournalStep>>> factory) => GetOrAddAsync(_journal, runId, factory);

    public void Evict(Guid runId)
    {
        _room.TryRemove(runId, out _);
        _journal.TryRemove(runId, out _);
        _touch.TryRemove(runId, out _);
    }

    private async Task<T> GetOrAddAsync<T>(ConcurrentDictionary<Guid, T> store, Guid runId, Func<Task<T>> factory)
    {
        if (store.TryGetValue(runId, out var hit))
        {
            _touch[runId] = Interlocked.Increment(ref _clock);
            return hit;
        }

        // Miss: compute outside any lock. A concurrent miss for the same run recomputes (last-writer-wins) — harmless,
        // both produce the same immutable terminal projection.
        var built = await factory().ConfigureAwait(false);
        store[runId] = built;
        _touch[runId] = Interlocked.Increment(ref _clock);

        Trim();
        return built;
    }

    /// <summary>Coarse LRU trim when either store passes the cap — drop the least-recently-touched runs from BOTH stores together.</summary>
    private void Trim()
    {
        if (_room.Count <= MaxEntries && _journal.Count <= MaxEntries) return;

        foreach (var runId in _touch.OrderBy(kv => kv.Value).Take(MaxEntries / 4).Select(kv => kv.Key).ToList())
            Evict(runId);
    }
}
