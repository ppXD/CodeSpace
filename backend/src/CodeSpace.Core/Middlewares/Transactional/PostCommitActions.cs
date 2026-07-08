using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Middlewares.Transactional;

/// <summary>
/// Request-scoped register of side effects that must run only AFTER the ambient transaction commits —
/// canonically, enqueuing a Hangfire job that targets a row written in this request. Enqueuing such a
/// job while the row is still uncommitted lets a worker fetch it before the row is visible: the engine's
/// <c>Enqueued → Running</c> CAS no-ops, and the run sits stuck until the reconciler's ~10-minute sweep.
///
/// <para>Scoped (one instance per request) so the enqueuing service and the draining
/// <see cref="TransactionalBehavior{TRequest,TResponse}"/> share one queue. On the rollback path the
/// behavior never drains, so a failed command fires no post-commit effect.</para>
/// </summary>
public interface IPostCommitActions
{
    /// <summary>
    /// Run <paramref name="action"/> after the ambient transaction commits. When there is NO open
    /// transaction (the caller's <c>SaveChanges</c> already auto-committed — e.g. a service invoked
    /// outside the <c>ICommand</c> pipeline), run it immediately, since the row is already durable.
    /// </summary>
    Task RunAfterCommitAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken);

    /// <summary>Drain every deferred action. Invoked by <see cref="TransactionalBehavior{TRequest,TResponse}"/> once, after commit.</summary>
    Task RunAllAsync(CancellationToken cancellationToken);
}

public sealed class PostCommitActions : IPostCommitActions, IScopedDependency
{
    private readonly List<Func<CancellationToken, Task>> _actions = new();
    private readonly CodeSpaceDbContext _db;
    private readonly ILogger<PostCommitActions> _logger;

    public PostCommitActions(CodeSpaceDbContext db, ILogger<PostCommitActions> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task RunAfterCommitAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        // Inside a transaction → defer to the drain; the row this action targets isn't durable yet.
        if (_db.Database.CurrentTransaction != null)
        {
            _actions.Add(action);
            return;
        }

        // No ambient transaction → already post-commit; run now.
        await action(cancellationToken).ConfigureAwait(false);
    }

    public async Task RunAllAsync(CancellationToken cancellationToken)
    {
        if (_actions.Count == 0) return;

        // Snapshot + clear first so an action that itself defers (re-entrant) can't loop, and a second
        // drain is a no-op.
        var pending = _actions.ToList();
        _actions.Clear();

        foreach (var action in pending)
        {
            try
            {
                await action(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The command already committed — a failed post-commit effect must not surface as a
                // command failure. Log and continue; the reconciler re-dispatches anything dropped here.
                _logger.LogWarning(ex, "Post-commit action failed; the reconciler will recover any dropped dispatch");
            }
        }
    }
}
