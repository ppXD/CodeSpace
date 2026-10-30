using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Middlewares.Transactional;

public sealed class TransactionCompletionCallbacks : IScopedDependency, IDisposable
{
    private readonly List<Func<CancellationToken, Task>> _committed = [];
    private readonly List<Action> _rolledBack = [];

    public void RegisterAfterCommit(Func<CancellationToken, Task> callback) => _committed.Add(callback);
    public void RegisterAfterRollback(Action callback) => _rolledBack.Add(callback);

    public async Task RunAfterCommitAsync(CancellationToken ct)
    {
        var callbacks = _committed.ToArray();
        _committed.Clear();
        _rolledBack.Clear();
        foreach (var callback in callbacks) await callback(ct).ConfigureAwait(false);
    }

    public void RunAfterRollback()
    {
        var callbacks = _rolledBack.ToArray();
        _committed.Clear();
        _rolledBack.Clear();
        foreach (var callback in callbacks) callback();
    }

    public void Dispose() => RunAfterRollback();
}
