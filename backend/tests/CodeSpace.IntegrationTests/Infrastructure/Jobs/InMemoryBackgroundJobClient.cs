using System.Linq.Expressions;
using Autofac;
using CodeSpace.Core.Constants;
using CodeSpace.Core.Services.Jobs;
using Hangfire.Storage;

namespace CodeSpace.IntegrationTests.Infrastructure.Jobs;

/// <summary>
/// Test double for <see cref="ICodeSpaceBackgroundJobClient"/> that records every Enqueue
/// call AND actually executes Task-returning enqueues — but DEFERRED until
/// <see cref="WaitForPendingAsync"/> is called. PostBoy / Smarties / Squid style.
///
/// <para><b>Why deferred?</b> In production a Hangfire worker pulls jobs off storage in its
/// own lifetime scope with its own DB connection. The dispatcher that enqueued the job is
/// often inside an uncommitted EF transaction; the worker only sees the row AFTER that
/// transaction commits. Running the job synchronously inside <c>Enqueue</c> would model a
/// fundamentally different transaction topology: the "worker" would share the dispatcher's
/// scope + connection, see uncommitted writes, and possibly succeed where production would
/// have raced. Deferring to <see cref="WaitForPendingAsync"/> ensures the test scope's outer
/// transaction commits FIRST, then the queued job runs in a fresh scope on a fresh connection
/// — exactly what a real Hangfire worker does.</para>
///
/// <para><b>Test pattern:</b></para>
/// <code>
/// using var scope = _fixture.BeginScopeAs(...);
/// var mediator = scope.Resolve&lt;IMediator&gt;();
/// await mediator.Send(new BindRepositoryCommand(...));   // queues registrar job
///
/// var client = _fixture.BeginScope().Resolve&lt;InMemoryBackgroundJobClient&gt;();
/// await client.WaitForPendingAsync();   // outer tx committed → registrar runs now
///
/// // assert final state (e.g. webhook is Registered)
/// </code>
///
/// <para><b>AutoExecute = false</b> opts out of execution entirely — Enqueue only records.
/// Use this when the test wants to assert intermediate state (e.g. "row is Enqueued after
/// dispatch but before worker pickup") or to test the dispatcher CAS in isolation.</para>
///
/// <para><b>ThrowOnEnqueue</b> — when non-null, the next Enqueue call throws this exception
/// (then the field clears). Exercises the dispatcher's revert-on-throw path.</para>
/// </summary>
public sealed class InMemoryBackgroundJobClient : ICodeSpaceBackgroundJobClient
{
    private readonly ILifetimeScope _scope;
    private readonly object _lock = new();
    private readonly List<EnqueuedCall> _calls = new();
    private readonly Queue<Func<Task>> _pending = new();

    public InMemoryBackgroundJobClient(ILifetimeScope scope) { _scope = scope; }

    /// <summary>
    /// When true (default), every Task-returning Enqueue overload queues a deferred
    /// execution. The execution actually runs only when <see cref="WaitForPendingAsync"/>
    /// is called. Set to false to disable queueing entirely (Enqueue is record-only).
    /// </summary>
    public bool AutoExecute { get; set; } = true;

    /// <summary>When non-null, the next Enqueue call throws this exception (then the field clears).</summary>
    public Exception? ThrowOnEnqueue { get; set; }

    /// <summary>Every Enqueue call observed during this fixture's lifetime. Tests can clear if needed.</summary>
    public IReadOnlyList<EnqueuedCall> Calls
    {
        get
        {
            lock (_lock) return _calls.ToList();
        }
    }

    /// <summary>Number of queued-but-not-yet-executed deferred jobs. Diagnostic only.</summary>
    public int PendingCount
    {
        get
        {
            lock (_lock) return _pending.Count;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _calls.Clear();
            _pending.Clear();
        }
    }

    /// <summary>
    /// Run every queued deferred execution to completion, in FIFO order. Each runs in a
    /// fresh <see cref="ILifetimeScope"/> (= fresh DbContext + connection), mirroring real
    /// Hangfire worker semantics. A job that itself enqueues more jobs appends to the queue,
    /// so chained dispatches drain in one call.
    ///
    /// <para>Safety cap: 1000 iterations to prevent infinite-recursion bugs in jobs from
    /// hanging tests forever.</para>
    /// </summary>
    public async Task WaitForPendingAsync()
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

        throw new InvalidOperationException($"WaitForPendingAsync exceeded {safetyCap} iterations — a job is enqueueing itself in a loop.");
    }

    public string Enqueue(Expression<Func<Task>> methodCall, string queue = HangfireConstants.DefaultQueue)
    {
        var jobId = Record(typeof(object), methodCall.Body);
        if (AutoExecute) QueueFuncTask(methodCall.Compile());
        return jobId;
    }

    public string Enqueue<T>(Expression<Func<T, Task>> methodCall, string queue = HangfireConstants.DefaultQueue)
    {
        var jobId = Record(typeof(T), methodCall.Body);
        if (AutoExecute) QueueFuncTTask(methodCall.Compile());
        return jobId;
    }

    public string Enqueue<T>(Expression<Action> methodCall, string queue = HangfireConstants.DefaultQueue) =>
        Record(typeof(T), methodCall.Body);

    public string Enqueue<T>(Expression<Action<T>> methodCall, string queue = HangfireConstants.DefaultQueue) =>
        Record(typeof(T), methodCall.Body);

    public string Schedule(Expression<Func<Task>> methodCall, TimeSpan delay, string queue = HangfireConstants.DefaultQueue) =>
        Record(typeof(object), methodCall.Body);

    public string Schedule(Expression<Func<Task>> methodCall, DateTimeOffset enqueueAt, string queue = HangfireConstants.DefaultQueue) =>
        Record(typeof(object), methodCall.Body);

    public string Schedule<T>(Expression<Func<T, Task>> methodCall, TimeSpan delay, string queue = HangfireConstants.DefaultQueue) =>
        Record(typeof(T), methodCall.Body);

    public string Schedule<T>(Expression<Func<T, Task>> methodCall, DateTimeOffset enqueueAt, string queue = HangfireConstants.DefaultQueue) =>
        Record(typeof(T), methodCall.Body);

    public string ContinueJobWith(string parentJobId, Expression<Func<Task>> methodCall, string queue = HangfireConstants.DefaultQueue) =>
        Record(typeof(object), methodCall.Body);

    public string ContinueJobWith<T>(string parentJobId, Expression<Func<T, Task>> methodCall, string queue = HangfireConstants.DefaultQueue) =>
        Record(typeof(T), methodCall.Body);

    public void AddOrUpdateRecurringJob<T>(string recurringJobId, Expression<Func<T, Task>> methodCall, string cronExpression, TimeZoneInfo? timeZone = null, string queue = HangfireConstants.DefaultQueue)
    {
        // No-op in tests — recurring jobs are exercised by directly invoking their
        // <c>Execute</c> method through the mediator command path.
    }

    public bool DeleteJob(string jobId) => false;

    public void RemoveRecurringJobIfExists(string jobId) { }

    public List<RecurringJobDto> GetRecurringJobs() => new();

    public StateData? GetJobState(string jobId) => null;

    private void QueueFuncTask(Func<Task> compiled)
    {
        Func<Task> deferred = async () =>
        {
            await using var execScope = _scope.BeginLifetimeScope();
            await compiled().ConfigureAwait(false);
        };
        lock (_lock) _pending.Enqueue(deferred);
    }

    private void QueueFuncTTask<T>(Func<T, Task> compiled)
    {
        Func<Task> deferred = async () =>
        {
            await using var execScope = _scope.BeginLifetimeScope();
            var instance = execScope.Resolve<T>()!;
            await compiled(instance).ConfigureAwait(false);
        };
        lock (_lock) _pending.Enqueue(deferred);
    }

    private string Record(Type serviceType, Expression body)
    {
        if (ThrowOnEnqueue is { } ex)
        {
            ThrowOnEnqueue = null;
            throw ex;
        }

        var (methodName, runId) = Decode(body);
        var jobId = Guid.NewGuid().ToString("N");

        lock (_lock)
        {
            _calls.Add(new EnqueuedCall
            {
                JobId = jobId,
                ServiceType = serviceType,
                MethodName = methodName,
                RunId = runId,
                EnqueuedAt = DateTimeOffset.UtcNow,
            });
        }

        return jobId;
    }

    /// <summary>
    /// Pull (methodName, firstGuidArg) out of the expression. Designed for the
    /// <c>IWorkflowEngine.ExecuteRunAsync(Guid runId, CancellationToken)</c> +
    /// <c>IRepositoryWebhookRegistrar.RunAsync(Guid webhookId, CancellationToken)</c>
    /// shape — tests can extract the id from <see cref="EnqueuedCall.RunId"/>.
    /// </summary>
    private static (string MethodName, Guid? RunId) Decode(Expression body)
    {
        if (body is not MethodCallExpression call)
            return ("<non-method-call>", null);

        Guid? runId = null;
        if (call.Arguments.Count > 0)
        {
            try
            {
                var firstArg = Expression.Lambda(call.Arguments[0]).Compile().DynamicInvoke();
                if (firstArg is Guid guid) runId = guid;
            }
            catch
            {
                // Best-effort: if the arg isn't trivially evaluable, leave runId null.
            }
        }

        return (call.Method.Name, runId);
    }
}

public sealed record EnqueuedCall
{
    public required string JobId { get; init; }
    public required Type ServiceType { get; init; }
    public required string MethodName { get; init; }
    public required Guid? RunId { get; init; }
    public required DateTimeOffset EnqueuedAt { get; init; }
}
