using System.Collections.Concurrent;
using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Workflows.ExternalTriggers;

/// <summary>
/// Process-local budget; only durable new runs consume capacity.
/// </summary>
public sealed class ExternalTriggerRunAdmission(TimeProvider? timeProvider = null) : ISingletonDependency, IDisposable
{
    public const int PermitsPerWindow = 60;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<Guid, WindowState> _windows = new();

    public Reservation? Reserve(Guid triggerId)
    {
        var state = _windows.GetOrAdd(triggerId, _ => new WindowState());
        lock (state)
        {
            var now = _clock.GetUtcNow();
            if (now >= state.Start + Window)
            {
                state.Start = now;
                state.Generation++;
                state.Successful = 0;
                state.Reserved = 0;
            }
            if (state.Successful + state.Reserved >= PermitsPerWindow) return null;
            state.Reserved++;
            return new Reservation(state, state.Generation);
        }
    }

    public void Dispose() => _windows.Clear();

    internal sealed class WindowState
    {
        public DateTimeOffset Start = DateTimeOffset.MinValue;
        public long Generation;
        public int Successful;
        public int Reserved;
    }

    public sealed class Reservation : IDisposable
    {
        private readonly WindowState _state;
        private readonly long _generation;
        private bool _completed;

        internal Reservation(WindowState state, long generation) { _state = state; _generation = generation; }

        public void Commit() => Complete(committed: true);
        public void Release() => Complete(committed: false);
        public void Dispose() => Release();

        private void Complete(bool committed)
        {
            lock (_state)
            {
                if (_completed) return;
                _completed = true;
                if (_generation != _state.Generation) return;
                _state.Reserved--;
                if (committed) _state.Successful++;
            }
        }
    }
}
