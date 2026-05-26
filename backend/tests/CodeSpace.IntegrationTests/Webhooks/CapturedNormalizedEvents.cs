using CodeSpace.Messages.Events;

namespace CodeSpace.IntegrationTests.Webhooks;

public sealed class CapturedNormalizedEvents
{
    private readonly object _lock = new();
    private readonly List<NormalizedEvent> _events = new();

    public void Add(NormalizedEvent normalized)
    {
        lock (_lock) _events.Add(normalized);
    }

    public IReadOnlyList<NormalizedEvent> Snapshot()
    {
        lock (_lock) return _events.ToList();
    }

    public void Clear()
    {
        lock (_lock) _events.Clear();
    }
}
