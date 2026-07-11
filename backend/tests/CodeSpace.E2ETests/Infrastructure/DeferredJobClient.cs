using System.Linq.Expressions;
using Autofac;
using CodeSpace.Core.Constants;
using CodeSpace.Core.Services.Jobs;
using Hangfire.States;
using Hangfire.Storage;

namespace CodeSpace.E2ETests.Infrastructure;

/// <summary>
/// E2E test double for <see cref="ICodeSpaceBackgroundJobClient"/> — records nothing, but EXECUTES every
/// Task-returning enqueue, DEFERRED until <see cref="DrainAsync"/>. Mirrors the integration suite's
/// <c>InMemoryBackgroundJobClient</c> deferral discipline: a real Hangfire worker only sees a row AFTER the
/// dispatcher's transaction commits, so we defer execution to a FRESH Autofac scope (= fresh DbContext +
/// connection) drained after the HTTP request returns. This lets the launch endpoint's post-commit dispatch →
/// engine run → agent.run suspend → executor → completion → resume → terminal chain run for real, end to end,
/// behind the real ASP.NET pipeline — only the Hangfire transport is faked.
/// </summary>
public sealed class DeferredJobClient : ICodeSpaceBackgroundJobClient
{
    private readonly ILifetimeScope _scope;
    private readonly object _lock = new();
    private readonly Queue<Func<Task>> _pending = new();

    public DeferredJobClient(ILifetimeScope scope) { _scope = scope; }

    /// <summary>Drain every queued deferred execution to completion (FIFO). A job that enqueues more jobs appends to the queue, so chained dispatches drain in one call. Safety cap prevents an infinite-recursion bug from hanging the test.</summary>
    public async Task DrainAsync()
    {
        const int safetyCap = 1000;
        for (var i = 0; i < safetyCap; i++)
        {
            Func<Task>? next;
            lock (_lock)
            {
                if (_pending.Count == 0) return;
                next = _pending.Dequeue();
            }
            await next().ConfigureAwait(false);
        }

        throw new InvalidOperationException($"DrainAsync exceeded {safetyCap} iterations — a job is enqueueing itself in a loop.");
    }

    public string Enqueue(Expression<Func<Task>> methodCall, string queue = HangfireConstants.DefaultQueue)
    {
        var compiled = methodCall.Compile();
        QueueDeferred(async () => { await using var s = _scope.BeginLifetimeScope(); await compiled().ConfigureAwait(false); });
        return NewId();
    }

    public string Enqueue<T>(Expression<Func<T, Task>> methodCall, string queue = HangfireConstants.DefaultQueue)
    {
        var compiled = methodCall.Compile();
        QueueDeferred(async () => { await using var s = _scope.BeginLifetimeScope(); await compiled(s.Resolve<T>()!).ConfigureAwait(false); });
        return NewId();
    }

    public string Enqueue<T>(Expression<Action> methodCall, string queue = HangfireConstants.DefaultQueue) => NewId();

    public string Enqueue<T>(Expression<Action<T>> methodCall, string queue = HangfireConstants.DefaultQueue) => NewId();

    public string Schedule(Expression<Func<Task>> methodCall, TimeSpan delay, string queue = HangfireConstants.DefaultQueue) => NewId();

    public string Schedule(Expression<Func<Task>> methodCall, DateTimeOffset enqueueAt, string queue = HangfireConstants.DefaultQueue) => NewId();

    public string Schedule<T>(Expression<Func<T, Task>> methodCall, TimeSpan delay, string queue = HangfireConstants.DefaultQueue) => NewId();

    public string Schedule<T>(Expression<Func<T, Task>> methodCall, DateTimeOffset enqueueAt, string queue = HangfireConstants.DefaultQueue) => NewId();

    public string ContinueJobWith(string parentJobId, Expression<Func<Task>> methodCall, string queue = HangfireConstants.DefaultQueue) => NewId();

    public string ContinueJobWith<T>(string parentJobId, Expression<Func<T, Task>> methodCall, string queue = HangfireConstants.DefaultQueue) => NewId();

    public void AddOrUpdateRecurringJob<T>(string recurringJobId, Expression<Func<T, Task>> methodCall, string cronExpression, TimeZoneInfo? timeZone = null, string queue = HangfireConstants.DefaultQueue) { }

    public bool DeleteJob(string jobId) => false;

    public void RemoveRecurringJobIfExists(string jobId) { }

    public List<RecurringJobDto> GetRecurringJobs() => new();

    public StateData? GetJobState(string jobId) => null;

    private void QueueDeferred(Func<Task> deferred)
    {
        lock (_lock) _pending.Enqueue(deferred);
    }

    private static string NewId() => Guid.NewGuid().ToString("N");
}
